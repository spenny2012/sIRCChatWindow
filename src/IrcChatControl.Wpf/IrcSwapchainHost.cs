using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Interop;
using System.Windows.Threading;

namespace IrcChatWpf
{
    /// <summary>
    /// Hosts the native renderer's child HWND; the renderer presents into it
    /// with a DXGI flip-model swapchain. The child window is hit-test
    /// transparent (HTTRANSPARENT), so mouse input routes to the WPF tree and
    /// the control's selection/wheel handlers keep working unchanged.
    ///
    /// The host is a transient View over the control's persistent renderer:
    /// BuildWindowCore attaches (or unparks) the View and DestroyWindowCore
    /// detaches it — the native side parks the live surface rather than
    /// destroying it, so scrollback, GPU stack, and glyph atlas survive the
    /// control leaving the visual tree.
    /// </summary>
    internal sealed class IrcSwapchainHost : HwndHost
    {
        // The render timer parks after this many consecutive frames with no
        // work, so an idle window stops waking the CPU at 60 Hz.
        private const int IdleTicksBeforePark = 30;

        private readonly SafeRendererHandle _renderer; // owned by IrcChatControl
        private DispatcherTimer _timer;
        private int _idleTicks;
        private int _parked;     // 1 while the timer is stopped; UI thread writes, producers read
        private int _wakeQueued; // 1 while a producer's BeginInvoke(Wake) is in flight
        private Action _wakeAction;
        private int _width;
        private int _height;
        private double _dpiScale;

        public IrcSwapchainHost(SafeRendererHandle renderer, int pixelWidth, int pixelHeight, double dpiScale)
        {
            _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
            _width = Math.Max(1, pixelWidth);
            _height = Math.Max(1, pixelHeight);
            _dpiScale = dpiScale > 0.0 ? dpiScale : 1.0;
        }

        /// <summary>Raised on the UI thread after each frame is presented.</summary>
        public event EventHandler FrameRendered;

        protected override HandleRef BuildWindowCore(HandleRef hwndParent)
        {
            if (!NativeMethods.AttachView(_renderer, hwndParent.Handle, _width, _height, (float)_dpiScale))
                throw new InvalidOperationException("Failed to attach the native IRC renderer view.");

            // Present the first CONTENT frame synchronously, before WPF maps
            // the window: a cold attach otherwise shows the theme-cleared
            // surface until the first timer tick (a visible flash when
            // switching windows), and an unparked surface would show one tick
            // of stale content. AttachView marked the renderer dirty and has
            // released its lock, so this renders the current scrollback now.
            NativeMethods.RenderFrame(_renderer, out _, out _, out _, out _);

            _timer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            _timer.Tick += OnTimerTick;
            _timer.Start();

            return new HandleRef(this, NativeMethods.GetChildHwnd(_renderer));
        }

        protected override void DestroyWindowCore(HandleRef hwnd)
        {
            _timer?.Stop();
            _timer = null;
            _parked = 0;     // a stray queued Wake no-ops on the null timer
            _wakeQueued = 0;
            _idleTicks = 0;
            // Parks the live surface (hide + reparent to the hidden holder);
            // the renderer, its scrollback, and the GPU stack stay warm for
            // the next attach. Only the control's finalizer destroys them.
            NativeMethods.DetachView(_renderer);
        }

        /// <summary>Producer-thread signal that lines were enqueued. The
        /// fenced read pairs with the fenced _parked publish in OnTimerTick
        /// (Dekker: the park probe can't miss the enqueue AND this read miss
        /// the flag); _wakeQueued keeps it to one BeginInvoke per parked
        /// period.</summary>
        public void NotifyLinesPending()
        {
            if (Interlocked.CompareExchange(ref _parked, 1, 1) == 1 &&
                Interlocked.Exchange(ref _wakeQueued, 1) == 0)
                Dispatcher.BeginInvoke(_wakeAction ??= Wake);
        }

        /// <summary>Restarts the parked render timer. UI thread only; every
        /// state-mutating entry point calls this so new work always gets a
        /// frame.</summary>
        public void Wake()
        {
            Volatile.Write(ref _wakeQueued, 0);
            if (Interlocked.Exchange(ref _parked, 0) == 1 && _timer != null)
            {
                _idleTicks = 0;
                _timer.Start();
            }
        }

        public void Resize(int pixelWidth, int pixelHeight, double dpiScale)
        {
            _width = Math.Max(1, pixelWidth);
            _height = Math.Max(1, pixelHeight);
            _dpiScale = dpiScale > 0.0 ? dpiScale : 1.0;
            NativeMethods.SetSize(_renderer, _width, _height, (float)_dpiScale);
            Wake();
        }

        public void ScrollByPixels(double deltaDips) { NativeMethods.ScrollByPixels(_renderer, (float)deltaDips); Wake(); }
        public void ScrollToOffset(double offsetDips) { NativeMethods.ScrollToOffset(_renderer, (float)offsetDips); Wake(); }
        public void ScrollToEnd() { NativeMethods.ScrollToEnd(_renderer); Wake(); }

        public void SelectionBegin(double xDips, double yDips) { NativeMethods.SelectionBegin(_renderer, (float)xDips, (float)yDips); Wake(); }
        public void SelectionUpdate(double xDips, double yDips) { NativeMethods.SelectionUpdate(_renderer, (float)xDips, (float)yDips); Wake(); }
        public void SelectionEnd() { NativeMethods.SelectionEnd(_renderer); Wake(); }

        /// <summary>Current selection as a string ("" when empty). Two-call
        /// sizing is safe: the ring only mutates inside RenderFrame/Clear on
        /// this same thread.</summary>
        public string SelectionGetText()
        {
            int size = NativeMethods.SelectionGetText(_renderer, null, 0);
            if (size <= 0)
                return string.Empty;

            var buffer = new byte[size];
            int written = NativeMethods.SelectionGetText(_renderer, buffer, size);
            return System.Text.Encoding.UTF8.GetString(buffer, 0, Math.Min(written, size));
        }

        public (double Content, double Viewport, double Offset, double LineHeight, bool Pinned) GetScrollInfo()
        {
            NativeMethods.GetScrollInfo(_renderer, out float content, out float viewport,
                out float offset, out float lineHeight, out int pinned);
            return (content, viewport, offset, lineHeight, pinned != 0);
        }

        private void OnTimerTick(object sender, EventArgs e)
        {
            if (_timer == null)
                return;

            if (NativeMethods.RenderFrame(_renderer, out _, out _, out _, out _))
            {
                _idleTicks = 0;
                FrameRendered?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (++_idleTicks < IdleTicksBeforePark)
                return;

            // Park. Publish the flag with a full fence BEFORE the final probe:
            // a producer that enqueued too late for the probe to drain is then
            // guaranteed to observe _parked == 1 and BeginInvoke a Wake.
            Interlocked.Exchange(ref _parked, 1);
            if (NativeMethods.RenderFrame(_renderer, out _, out _, out _, out _))
            {
                Volatile.Write(ref _parked, 0);
                _idleTicks = 0;
                FrameRendered?.Invoke(this, EventArgs.Empty);
                return;
            }
            _timer.Stop();
        }
    }
}

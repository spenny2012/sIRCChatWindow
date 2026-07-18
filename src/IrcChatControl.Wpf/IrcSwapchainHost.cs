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
    /// </summary>
    internal sealed class IrcSwapchainHost : HwndHost
    {
        // The render timer parks after this many consecutive frames with no
        // work, so an idle window stops waking the CPU at 60 Hz.
        private const int IdleTicksBeforePark = 30;

        private IntPtr _renderer;
        private DispatcherTimer _timer;
        private int _idleTicks;
        private int _parked;     // 1 while the timer is stopped; UI thread writes, producers read
        private int _wakeQueued; // 1 while a producer's BeginInvoke(Wake) is in flight
        private Action _wakeAction;
        private int _width;
        private int _height;
        private double _dpiScale;
        // Defaults mirror the native renderer's initial theme (IrcPalette).
        private uint _fgArgb = 0xFFF2F2F2;
        private uint _bgArgb = 0xFF141414;
        private uint _selectionArgb = 0x59598CF2;
        private string _fontFamily = "Consolas";
        private float _fontSize = 14f;
        private uint _maxLines = 50000;

        public IrcSwapchainHost(int pixelWidth, int pixelHeight, double dpiScale)
        {
            _width = Math.Max(1, pixelWidth);
            _height = Math.Max(1, pixelHeight);
            _dpiScale = dpiScale > 0.0 ? dpiScale : 1.0;
        }

        /// <summary>Raised on the UI thread after each frame is presented.</summary>
        public event EventHandler FrameRendered;

        protected override HandleRef BuildWindowCore(HandleRef hwndParent)
        {
            _renderer = NativeMethods.CreateRenderer(hwndParent.Handle, _width, _height, (float)_dpiScale);
            if (_renderer == IntPtr.Zero)
                throw new InvalidOperationException("Failed to create native IRC renderer.");

            // Colors may have been set before the native renderer existed.
            NativeMethods.SetBackgroundColor(_renderer, _bgArgb);
            NativeMethods.SetForegroundColor(_renderer, _fgArgb);
            NativeMethods.SetSelectionColor(_renderer, _selectionArgb);
            NativeMethods.SetFontFamily(_renderer, _fontFamily);
            NativeMethods.SetFontSize(_renderer, _fontSize);
            NativeMethods.SetMaxLines(_renderer, _maxLines);

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
            if (_renderer != IntPtr.Zero)
            {
                NativeMethods.DestroyRenderer(_renderer); // also destroys the child HWND
                _renderer = IntPtr.Zero;
            }
        }

        public void AddLine(string text)
        {
            if (string.IsNullOrEmpty(text) || _renderer == IntPtr.Zero)
                return;

            // Encode on the stack: this runs thousands of times per second from
            // producer threads, and a heap byte[] per line just feeds the GC. The
            // native side caps lines at 512 bytes, so anything past that is dropped.
            Span<byte> buffer = stackalloc byte[512];
            System.Text.Unicode.Utf8.FromUtf16(text.AsSpan(), buffer,
                out _, out int bytesWritten);
            if (bytesWritten > 0)
            {
                NativeMethods.AddLine(_renderer, ref MemoryMarshal.GetReference(buffer), bytesWritten);

                // Runs on producer threads. The fenced read pairs with the
                // fenced _parked publish in OnTimerTick (Dekker: the park probe
                // can't miss this enqueue AND this read miss the flag), and
                // _wakeQueued keeps it to one BeginInvoke per parked period.
                if (Interlocked.CompareExchange(ref _parked, 1, 1) == 1 &&
                    Interlocked.Exchange(ref _wakeQueued, 1) == 0)
                    Dispatcher.BeginInvoke(_wakeAction ??= Wake);
            }
        }

        /// <summary>Restarts the parked render timer. UI thread only; every
        /// state-mutating entry point calls this so new work always gets a
        /// frame.</summary>
        private void Wake()
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
            if (_renderer != IntPtr.Zero)
            {
                NativeMethods.SetSize(_renderer, _width, _height, (float)_dpiScale);
                Wake();
            }
        }

        public void ScrollByPixels(double deltaDips) { if (_renderer != IntPtr.Zero) { NativeMethods.ScrollByPixels(_renderer, (float)deltaDips); Wake(); } }
        public void ScrollToOffset(double offsetDips) { if (_renderer != IntPtr.Zero) { NativeMethods.ScrollToOffset(_renderer, (float)offsetDips); Wake(); } }
        public void ScrollToEnd() { if (_renderer != IntPtr.Zero) { NativeMethods.ScrollToEnd(_renderer); Wake(); } }
        public void Clear() { if (_renderer != IntPtr.Zero) { NativeMethods.Clear(_renderer); Wake(); } }

        public void SetBackgroundColor(uint argb)
        {
            _bgArgb = argb;
            if (_renderer != IntPtr.Zero)
            {
                NativeMethods.SetBackgroundColor(_renderer, argb);
                Wake();
            }
        }

        public void SetForegroundColor(uint argb)
        {
            _fgArgb = argb;
            if (_renderer != IntPtr.Zero)
            {
                NativeMethods.SetForegroundColor(_renderer, argb);
                Wake();
            }
        }

        public void SetSelectionColor(uint argb)
        {
            _selectionArgb = argb;
            if (_renderer != IntPtr.Zero)
            {
                NativeMethods.SetSelectionColor(_renderer, argb);
                Wake();
            }
        }

        public void SetFontFamily(string family)
        {
            _fontFamily = family;
            if (_renderer != IntPtr.Zero)
            {
                NativeMethods.SetFontFamily(_renderer, family);
                Wake();
            }
        }

        public void SetFontSize(float size)
        {
            _fontSize = size;
            if (_renderer != IntPtr.Zero)
            {
                NativeMethods.SetFontSize(_renderer, size);
                Wake();
            }
        }

        public void SetMaxLines(uint maxLines)
        {
            _maxLines = maxLines;
            if (_renderer != IntPtr.Zero)
            {
                NativeMethods.SetMaxLines(_renderer, maxLines);
                Wake();
            }
        }

        public int LineCount => _renderer != IntPtr.Zero ? NativeMethods.GetLineCount(_renderer) : 0;

        public void SelectionBegin(double xDips, double yDips) { if (_renderer != IntPtr.Zero) { NativeMethods.SelectionBegin(_renderer, (float)xDips, (float)yDips); Wake(); } }
        public void SelectionUpdate(double xDips, double yDips) { if (_renderer != IntPtr.Zero) { NativeMethods.SelectionUpdate(_renderer, (float)xDips, (float)yDips); Wake(); } }
        public void SelectionEnd() { if (_renderer != IntPtr.Zero) { NativeMethods.SelectionEnd(_renderer); Wake(); } }

        /// <summary>Current selection as a string ("" when empty). Two-call
        /// sizing is safe: the ring only mutates inside RenderFrame/Clear on
        /// this same thread.</summary>
        public string SelectionGetText()
        {
            if (_renderer == IntPtr.Zero)
                return string.Empty;

            int size = NativeMethods.SelectionGetText(_renderer, null, 0);
            if (size <= 0)
                return string.Empty;

            var buffer = new byte[size];
            int written = NativeMethods.SelectionGetText(_renderer, buffer, size);
            return System.Text.Encoding.UTF8.GetString(buffer, 0, Math.Min(written, size));
        }

        public (double Content, double Viewport, double Offset, double LineHeight, bool Pinned) GetScrollInfo()
        {
            if (_renderer == IntPtr.Zero)
                return (0.0, 0.0, 0.0, 0.0, true);

            NativeMethods.GetScrollInfo(_renderer, out float content, out float viewport,
                out float offset, out float lineHeight, out int pinned);
            return (content, viewport, offset, lineHeight, pinned != 0);
        }

        private void OnTimerTick(object sender, EventArgs e)
        {
            if (_renderer == IntPtr.Zero)
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

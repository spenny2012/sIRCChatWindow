using System;
using System.Runtime.InteropServices;
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
        private IntPtr _renderer;
        private DispatcherTimer _timer;
        private int _width;
        private int _height;
        private double _dpiScale;
        // Defaults mirror the native renderer's initial theme (IrcPalette).
        private uint _fgArgb = 0xFFF2F2F2;
        private uint _bgArgb = 0xFF141414;

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
                NativeMethods.AddLine(_renderer, ref MemoryMarshal.GetReference(buffer), bytesWritten);
        }

        public void Resize(int pixelWidth, int pixelHeight, double dpiScale)
        {
            _width = Math.Max(1, pixelWidth);
            _height = Math.Max(1, pixelHeight);
            _dpiScale = dpiScale > 0.0 ? dpiScale : 1.0;
            if (_renderer != IntPtr.Zero)
                NativeMethods.SetSize(_renderer, _width, _height, (float)_dpiScale);
        }

        public void ScrollByPixels(double deltaDips) { if (_renderer != IntPtr.Zero) NativeMethods.ScrollByPixels(_renderer, (float)deltaDips); }
        public void ScrollToOffset(double offsetDips) { if (_renderer != IntPtr.Zero) NativeMethods.ScrollToOffset(_renderer, (float)offsetDips); }
        public void ScrollToEnd() { if (_renderer != IntPtr.Zero) NativeMethods.ScrollToEnd(_renderer); }
        public void Clear() { if (_renderer != IntPtr.Zero) NativeMethods.Clear(_renderer); }

        public void SetBackgroundColor(uint argb)
        {
            _bgArgb = argb;
            if (_renderer != IntPtr.Zero)
                NativeMethods.SetBackgroundColor(_renderer, argb);
        }

        public void SetForegroundColor(uint argb)
        {
            _fgArgb = argb;
            if (_renderer != IntPtr.Zero)
                NativeMethods.SetForegroundColor(_renderer, argb);
        }
        public int LineCount => _renderer != IntPtr.Zero ? NativeMethods.GetLineCount(_renderer) : 0;

        public void SelectionBegin(double xDips, double yDips) { if (_renderer != IntPtr.Zero) NativeMethods.SelectionBegin(_renderer, (float)xDips, (float)yDips); }
        public void SelectionUpdate(double xDips, double yDips) { if (_renderer != IntPtr.Zero) NativeMethods.SelectionUpdate(_renderer, (float)xDips, (float)yDips); }
        public void SelectionEnd() { if (_renderer != IntPtr.Zero) NativeMethods.SelectionEnd(_renderer); }

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
                FrameRendered?.Invoke(this, EventArgs.Empty);
        }
    }
}

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace IrcChatWpf
{
    public partial class IrcChatControl : UserControl
    {
        private IrcSwapchainHost _ircHost;
        private bool _updatingScroll;
        private bool _selecting;
        private Color? _fgColor; // set before the host exists → applied on creation
        private Color? _bgColor;
        private Color? _selectionColor;
        private string _fontFamily; // null = use the native default (Consolas)
        private double? _fontSize;
        private readonly DispatcherTimer _dragScrollTimer;

        public IrcChatControl()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            MouseWheel += OnMouseWheel;
            KeyDown += OnKeyDown;
            MouseLeftButtonDown += OnMouseLeftButtonDown;
            MouseLeftButtonUp += OnMouseLeftButtonUp;
            MouseMove += OnMouseMove;
            LostMouseCapture += OnLostMouseCapture;

            // Extends the selection while the captured pointer is dragged past
            // the top/bottom edge (MouseMove stops firing when the mouse rests).
            _dragScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _dragScrollTimer.Tick += OnDragScrollTick;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            RecreateOrResizeSurface();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            CancelSelectionDrag();
            if (_ircHost != null)
            {
                _ircHost.FrameRendered -= OnFrameRendered;
                ImageHost.Child = null;
                _ircHost.Dispose();
                _ircHost = null;
            }
        }

        public void AddLine(string text)
        {
            _ircHost?.AddLine(text);
        }

        public void Clear()
        {
            _ircHost?.Clear();
        }

        public int LineCount => _ircHost?.LineCount ?? 0;

        /// <summary>Sets the chat surface's background color. Applies
        /// immediately to all content (explicit mIRC/ANSI colors are
        /// unaffected). Safe to call before the control is loaded.</summary>
        public void SetBackgroundColor(Color color)
        {
            _bgColor = color;
            var brush = new SolidColorBrush(color);
            brush.Freeze(); // frozen brushes skip change-tracking overhead
            ImageHost.Background = brush;
            _ircHost?.SetBackgroundColor(PackArgb(color));
        }

        /// <summary>Sets the default text color (text without explicit
        /// mIRC/ANSI colors). Applies immediately to all content. Safe to
        /// call before the control is loaded.</summary>
        public void SetForegroundColor(Color color)
        {
            _fgColor = color;
            _ircHost?.SetForegroundColor(PackArgb(color));
        }

        /// <summary>Sets the tint drawn over selected text during mouse-drag
        /// selection. Unlike the background/foreground colors, the alpha
        /// channel is respected (not forced opaque) so the tint can overlay
        /// already-drawn glyphs. Safe to call before the control is
        /// loaded.</summary>
        public void SetSelectionColor(Color color)
        {
            _selectionColor = color;
            _ircHost?.SetSelectionColor(PackArgba(color));
        }

        /// <summary>Sets the rendering font family (e.g. "Cascadia Mono").
        /// Null/empty is ignored. Applies immediately: rebuilds glyph layout
        /// and re-wraps all scrollback, since word-wrap is column-based on the
        /// font's monospace advance width. A non-monospace font is allowed but
        /// renders with uneven glyph spacing (layout stays fixed-column).
        /// Safe to call before the control is loaded.</summary>
        public void SetFontFamily(string fontFamily)
        {
            if (string.IsNullOrEmpty(fontFamily))
                return;
            _fontFamily = fontFamily;
            _ircHost?.SetFontFamily(fontFamily);
        }

        /// <summary>Sets the rendering font size in DIPs. Non-positive values
        /// are ignored. Applies immediately, like <see cref="SetFontFamily"/>.
        /// Safe to call before the control is loaded.</summary>
        public void SetFontSize(double size)
        {
            if (size <= 0.0)
                return;
            _fontSize = size;
            _ircHost?.SetFontSize((float)size);
        }

        // Opaque: the swapchain has no per-pixel alpha, so a translucent
        // default would silently misrender.
        private static uint PackArgb(Color c) =>
            0xFF000000u | (uint)c.R << 16 | (uint)c.G << 8 | c.B;

        private static uint PackArgba(Color c) =>
            (uint)c.A << 24 | (uint)c.R << 16 | (uint)c.G << 8 | c.B;

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Swapchain ResizeBuffers reuses the buffer allocations; cheap
            // enough to run live on every layout pass of a drag.
            RecreateOrResizeSurface();
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            CancelSelectionDrag();
            RecreateOrResizeSurface(); // SetSize re-derives DIP metrics from the new scale
        }

        private bool TryGetViewportPixels(out int px, out int py, out DpiScale dpi)
        {
            dpi = VisualTreeHelper.GetDpi(this);
            double w = ImageHost.ActualWidth - ImageHost.BorderThickness.Left - ImageHost.BorderThickness.Right;
            double h = ImageHost.ActualHeight - ImageHost.BorderThickness.Top - ImageHost.BorderThickness.Bottom;
            px = Math.Max(1, (int)Math.Round(w * dpi.DpiScaleX));
            py = Math.Max(1, (int)Math.Round(h * dpi.DpiScaleY));
            return w >= 1 && h >= 1;
        }

        private void RecreateOrResizeSurface()
        {
            if (!TryGetViewportPixels(out int px, out int py, out DpiScale dpi))
                return;

            if (_ircHost == null)
            {
                _ircHost = new IrcSwapchainHost(px, py, dpi.DpiScaleX);
                if (_bgColor is Color bg) _ircHost.SetBackgroundColor(PackArgb(bg));
                if (_fgColor is Color fg) _ircHost.SetForegroundColor(PackArgb(fg));
                if (_selectionColor is Color sel) _ircHost.SetSelectionColor(PackArgba(sel));
                if (_fontFamily != null) _ircHost.SetFontFamily(_fontFamily);
                if (_fontSize is double fs) _ircHost.SetFontSize((float)fs);
                _ircHost.FrameRendered += OnFrameRendered;
                ImageHost.Child = _ircHost;
            }
            else
            {
                _ircHost.Resize(px, py, dpi.DpiScaleX);
            }
        }

        private void OnMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_ircHost == null)
                return;

            var info = _ircHost.GetScrollInfo();
            double lines = SystemParameters.WheelScrollLines;
            double step = lines > 0 ? lines * info.LineHeight : info.Viewport;
            _ircHost.ScrollByPixels(e.Delta / 120.0 * step);
            e.Handled = true;
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (_ircHost == null)
                return;

            var info = _ircHost.GetScrollInfo();
            switch (e.Key)
            {
                case Key.PageUp:
                    _ircHost.ScrollByPixels(info.Viewport);
                    e.Handled = true;
                    break;
                case Key.PageDown:
                    _ircHost.ScrollByPixels(-info.Viewport);
                    e.Handled = true;
                    break;
                case Key.Home:
                    _ircHost.ScrollToOffset(float.MaxValue);
                    e.Handled = true;
                    break;
                case Key.End:
                    _ircHost.ScrollToEnd();
                    e.Handled = true;
                    break;
            }
        }

        private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // A plain UserControl does not take keyboard focus on click; grab it
            // so PageUp/PageDown/Home/End reach OnKeyDown.
            Focus();

            if (_ircHost == null || !ImageHost.IsMouseOver)
                return;

            // The host element sits exactly on the viewport (inside the 1 px
            // border), so its DIP coordinates are the renderer's viewport DIPs.
            var p = e.GetPosition(_ircHost);
            _ircHost.SelectionBegin(p.X, p.Y);
            _selecting = true;
            CaptureMouse();
            e.Handled = true;
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (!_selecting || _ircHost == null)
                return;

            var p = e.GetPosition(_ircHost);
            _ircHost.SelectionUpdate(p.X, p.Y);

            if (p.Y < 0 || p.Y > _ircHost.GetScrollInfo().Viewport)
                _dragScrollTimer.Start();
            else
                _dragScrollTimer.Stop();
        }

        private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_selecting)
                return;

            // Clear the flag before releasing capture so OnLostMouseCapture
            // doesn't treat this as a cancelled drag.
            _selecting = false;
            _dragScrollTimer.Stop();
            ReleaseMouseCapture();

            if (_ircHost == null)
                return;

            var p = e.GetPosition(_ircHost);
            _ircHost.SelectionUpdate(p.X, p.Y);
            string text = _ircHost.SelectionGetText();
            _ircHost.SelectionEnd();
            if (text.Length > 0)
                TrySetClipboard(text);
            e.Handled = true;
        }

        private void OnLostMouseCapture(object sender, MouseEventArgs e)
        {
            if (!_selecting)
                return;

            // Capture stolen mid-drag (Alt-Tab, popup): cancel without copying.
            _selecting = false;
            _dragScrollTimer.Stop();
            _ircHost?.SelectionEnd();
        }

        private void OnDragScrollTick(object sender, EventArgs e)
        {
            if (!_selecting || _ircHost == null)
            {
                _dragScrollTimer.Stop();
                return;
            }

            var p = Mouse.GetPosition(_ircHost);
            var info = _ircHost.GetScrollInfo();

            // Scroll offset is distance-from-bottom: positive delta scrolls up.
            // The overshoot doubles as the speed, so a farther drag pans faster.
            double delta;
            if (p.Y < 0)
                delta = -p.Y;
            else if (p.Y > info.Viewport)
                delta = info.Viewport - p.Y;
            else
            {
                _dragScrollTimer.Stop();
                return;
            }

            _ircHost.ScrollByPixels(delta);
            _ircHost.SelectionUpdate(p.X, Math.Max(0.0, Math.Min(p.Y, info.Viewport)));
        }

        private void CancelSelectionDrag()
        {
            if (!_selecting)
                return;

            _selecting = false;
            _dragScrollTimer.Stop();
            if (IsMouseCaptured)
                ReleaseMouseCapture();
            _ircHost?.SelectionEnd();
        }

        private static void TrySetClipboard(string text)
        {
            // The clipboard is a shared resource: opening it fails transiently
            // (CLIPBRD_E_CANT_OPEN) while another process holds it.
            try
            {
                Clipboard.SetText(text);
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                try { Clipboard.SetText(text); }
                catch (System.Runtime.InteropServices.COMException) { }
            }
        }

        private void OnScroll(object sender, ScrollEventArgs e)
        {
            if (_updatingScroll || _ircHost == null)
                return;

            _ircHost.ScrollToOffset(ChatScrollBar.Maximum - e.NewValue);
        }

        private void OnFrameRendered(object sender, EventArgs e)
        {
            // While the thumb is being dragged, input flows one-way via OnScroll.
            if (_ircHost == null || ChatScrollBar.IsMouseCaptureWithin)
                return;

            var info = _ircHost.GetScrollInfo();
            double maximum = Math.Max(0.0, info.Content - info.Viewport);
            double value = maximum - info.Offset;

            _updatingScroll = true;
            try
            {
                const double epsilon = 0.25;
                if (Math.Abs(ChatScrollBar.Maximum - maximum) > epsilon)
                    ChatScrollBar.Maximum = maximum;
                if (Math.Abs(ChatScrollBar.ViewportSize - info.Viewport) > epsilon)
                    ChatScrollBar.ViewportSize = info.Viewport;
                if (Math.Abs(ChatScrollBar.SmallChange - info.LineHeight) > epsilon)
                    ChatScrollBar.SmallChange = info.LineHeight;
                if (Math.Abs(ChatScrollBar.LargeChange - info.Viewport) > epsilon)
                    ChatScrollBar.LargeChange = info.Viewport;
                if (Math.Abs(ChatScrollBar.Value - value) > epsilon)
                    ChatScrollBar.Value = value;
            }
            finally
            {
                _updatingScroll = false;
            }
        }
    }
}

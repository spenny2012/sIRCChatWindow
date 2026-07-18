using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace IrcChatWpf
{
    /// <summary>Console-style IRC chat surface rendered by a native
    /// Direct2D/DirectWrite renderer. Theme it via the standard WPF
    /// <see cref="Control.Background"/>, <see cref="Control.Foreground"/>,
    /// <see cref="Control.FontFamily"/>, and <see cref="Control.FontSize"/>
    /// properties (bindable; solid brushes reach the renderer) or the
    /// equivalent Set* methods.</summary>
    public partial class IrcChatControl : UserControl
    {
        private const double DefaultFontSizeDips = 14.0; // mirrors the native/host default
        private const double MinZoomFontSize = 6.0;
        private const double MaxZoomFontSize = 72.0;

        private IrcSwapchainHost _ircHost;
        private bool _updatingScroll;
        private bool _selecting;
        private double _viewportDips; // cached so MouseMove skips a per-move native call
        private int _wheelZoomRemainder; // accumulates sub-notch deltas (precision touchpads)
        private int? _maxLines; // set before the host exists → applied on creation
        private readonly DispatcherTimer _dragScrollTimer;

        static IrcChatControl()
        {
            // Route the standard theming properties to the native renderer.
            // Metadata merge keeps the base flags (Inherits on the font
            // properties), so an ancestor's FontFamily/FontSize cascades in
            // like any WPF control; the defaults are re-based to the
            // renderer's own console theme so an unstyled control keeps
            // Consolas 14 on the dark palette. Callbacks fire only when a
            // value changes — the AddLine/render hot path never touches DPs.
            BackgroundProperty.OverrideMetadata(typeof(IrcChatControl),
                new FrameworkPropertyMetadata(FrozenBrush(Color.FromRgb(0x14, 0x14, 0x14)), OnBackgroundChanged));
            ForegroundProperty.OverrideMetadata(typeof(IrcChatControl),
                new FrameworkPropertyMetadata(FrozenBrush(Color.FromRgb(0xF2, 0xF2, 0xF2)), OnForegroundChanged));
            FontFamilyProperty.OverrideMetadata(typeof(IrcChatControl),
                new FrameworkPropertyMetadata(new FontFamily("Consolas"), OnFontFamilyChanged));
            FontSizeProperty.OverrideMetadata(typeof(IrcChatControl),
                new FrameworkPropertyMetadata(DefaultFontSizeDips, OnFontSizeChanged));
        }

        /// <summary>Initializes the control. The native rendering surface is
        /// created when the control is loaded into a window.</summary>
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

        /// <summary>Appends one line to the scrollback. Thread-safe,
        /// lock-free, and allocation-free on the hot path: any thread may call
        /// this at thousands of lines per second. mIRC control codes and ANSI
        /// SGR sequences are parsed inline; lines are capped at 512 UTF-8
        /// bytes. Lines added before the control is loaded are dropped.</summary>
        public void AddLine(string text)
        {
            _ircHost?.AddLine(text);
        }

        /// <summary>Removes every line from the scrollback and decommits the
        /// backing text arena. UI thread only.</summary>
        public void Clear()
        {
            _ircHost?.Clear();
        }

        /// <summary>Number of lines currently held in the scrollback ring
        /// buffer (after any eviction by <see cref="SetMaxLines"/>).</summary>
        public int LineCount => _ircHost?.LineCount ?? 0;

        /// <summary>Scrolls to the newest line and re-pins auto-follow, so
        /// subsequent <see cref="AddLine"/> calls keep the view at the
        /// bottom.</summary>
        public void ScrollToEnd()
        {
            _ircHost?.ScrollToEnd();
        }

        /// <summary>The tint drawn over selected text during mouse-drag
        /// selection. The alpha channel is respected (not forced opaque) so
        /// the tint overlays already-drawn glyphs. Only solid brushes reach
        /// the renderer.</summary>
        public static readonly DependencyProperty SelectionBrushProperty =
            DependencyProperty.Register(nameof(SelectionBrush), typeof(Brush), typeof(IrcChatControl),
                new FrameworkPropertyMetadata(FrozenBrush(Color.FromArgb(0x59, 0x59, 0x8C, 0xF2)), OnSelectionBrushChanged));

        /// <summary>See <see cref="SelectionBrushProperty"/>.</summary>
        public Brush SelectionBrush
        {
            get => (Brush)GetValue(SelectionBrushProperty);
            set => SetValue(SelectionBrushProperty, value);
        }

        /// <summary>Sets the chat surface's background color. Equivalent to
        /// setting <see cref="Control.Background"/> with a solid brush
        /// (applies immediately; explicit mIRC/ANSI colors are unaffected).
        /// Safe to call before the control is loaded.</summary>
        public void SetBackgroundColor(Color color) =>
            SetCurrentValue(BackgroundProperty, FrozenBrush(color));

        /// <summary>Sets the default text color (text without explicit
        /// mIRC/ANSI colors). Equivalent to setting
        /// <see cref="Control.Foreground"/> with a solid brush. Safe to call
        /// before the control is loaded.</summary>
        public void SetForegroundColor(Color color) =>
            SetCurrentValue(ForegroundProperty, FrozenBrush(color));

        /// <summary>Sets the selection tint; equivalent to setting
        /// <see cref="SelectionBrush"/> with a solid brush.</summary>
        public void SetSelectionColor(Color color) =>
            SetCurrentValue(SelectionBrushProperty, FrozenBrush(color));

        /// <summary>Sets the rendering font family (e.g. "Cascadia Mono");
        /// equivalent to setting <see cref="Control.FontFamily"/>. Null/empty
        /// is ignored. Applies immediately: rebuilds glyph layout and re-wraps
        /// all scrollback, since word-wrap is column-based on the font's
        /// monospace advance width. A non-monospace font is allowed but
        /// renders with uneven glyph spacing (layout stays fixed-column).
        /// Safe to call before the control is loaded.</summary>
        public void SetFontFamily(string fontFamily)
        {
            if (string.IsNullOrEmpty(fontFamily))
                return;
            SetCurrentValue(FontFamilyProperty, new FontFamily(fontFamily));
        }

        /// <summary>Enables growing/shrinking the font with Ctrl+mouse-wheel
        /// over the chat area (default true). The size is clamped to
        /// [6, 72] DIPs; plain wheel scrolling is unaffected.</summary>
        public bool EnableFontZoom { get; set; } = true;

        /// <summary>The current rendering font size in DIPs (the
        /// <see cref="Control.FontSize"/> value, including Ctrl+wheel
        /// zoom).</summary>
        public double CurrentFontSize => FontSize;

        /// <summary>Sets the rendering font size in DIPs; equivalent to
        /// setting <see cref="Control.FontSize"/>. Non-positive values are
        /// ignored. Applies immediately, like <see cref="SetFontFamily"/>.
        /// Safe to call before the control is loaded.</summary>
        public void SetFontSize(double size)
        {
            if (size <= 0.0)
                return;
            SetCurrentValue(FontSizeProperty, size);
        }

        private static SolidColorBrush FrozenBrush(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze(); // frozen brushes skip change-tracking overhead
            return brush;
        }

        private static void OnBackgroundChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var c = (IrcChatControl)d;
            if (c.ImageHost != null)
                c.ImageHost.Background = e.NewValue as Brush;
            if (e.NewValue is SolidColorBrush brush)
                c._ircHost?.SetBackgroundColor(PackArgb(brush.Color));
        }

        private static void OnForegroundChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var c = (IrcChatControl)d;
            if (e.NewValue is SolidColorBrush brush)
                c._ircHost?.SetForegroundColor(PackArgb(brush.Color));
        }

        private static void OnSelectionBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var c = (IrcChatControl)d;
            if (e.NewValue is SolidColorBrush brush)
                c._ircHost?.SetSelectionColor(PackArgba(brush.Color));
        }

        private static void OnFontFamilyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var c = (IrcChatControl)d;
            string family = (e.NewValue as FontFamily)?.Source;
            if (!string.IsNullOrEmpty(family))
                c._ircHost?.SetFontFamily(family);
        }

        private static void OnFontSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var c = (IrcChatControl)d;
            double size = (double)e.NewValue;
            if (size > 0.0)
                c._ircHost?.SetFontSize((float)size);
        }

        /// <summary>Sets the scrollback retention limit. Once reached, the
        /// oldest lines are evicted as new lines arrive at the bottom.
        /// Clamped natively to [1, 50000]; the 16 MiB text arena can evict
        /// earlier for extremely long lines. Shrinking below the current
        /// line count evicts immediately. Non-positive values are ignored.
        /// Safe to call before the control is loaded.</summary>
        public void SetMaxLines(int maxLines)
        {
            if (maxLines <= 0)
                return;
            _maxLines = maxLines;
            _ircHost?.SetMaxLines((uint)maxLines);
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

        /// <summary>Re-derives the native surface's pixel size and DIP metrics
        /// for the monitor's new DPI scale.</summary>
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
                // DP callbacks fire only on changes, so a fresh host needs the
                // current values (defaults, styles, or pre-load sets) pushed.
                if (Background is SolidColorBrush bg) _ircHost.SetBackgroundColor(PackArgb(bg.Color));
                if (Foreground is SolidColorBrush fg) _ircHost.SetForegroundColor(PackArgb(fg.Color));
                if (SelectionBrush is SolidColorBrush sel) _ircHost.SetSelectionColor(PackArgba(sel.Color));
                string family = FontFamily?.Source;
                if (!string.IsNullOrEmpty(family)) _ircHost.SetFontFamily(family);
                if (FontSize > 0.0) _ircHost.SetFontSize((float)FontSize);
                if (_maxLines is int ml) _ircHost.SetMaxLines((uint)ml);
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

            if (EnableFontZoom && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                // Accumulate so precision touchpads (deltas < 120) still zoom,
                // and a direction reversal responds immediately instead of
                // unwinding the leftover from the previous direction.
                if (Math.Sign(e.Delta) != Math.Sign(_wheelZoomRemainder))
                    _wheelZoomRemainder = 0;
                _wheelZoomRemainder += e.Delta;
                int notches = _wheelZoomRemainder / 120;
                _wheelZoomRemainder -= notches * 120;

                if (notches != 0)
                {
                    double next = Math.Max(MinZoomFontSize,
                        Math.Min(MaxZoomFontSize, CurrentFontSize + notches));
                    // Skipping the setter at the clamp limits avoids even the
                    // native call; the rebuild itself is deferred and coalesced
                    // to one per frame by the renderer.
                    if (next != CurrentFontSize)
                        SetFontSize(next);
                }
                e.Handled = true;
                return;
            }

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
            _viewportDips = _ircHost.GetScrollInfo().Viewport;
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

            if (p.Y < 0 || p.Y > _viewportDips)
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
            _viewportDips = info.Viewport;
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

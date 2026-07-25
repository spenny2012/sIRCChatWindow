using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
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
    public partial class IrcChatControl : UserControl, IDisposable
    {
        private const double DefaultFontSizeDips = 14.0; // mirrors the native/host default
        private const double MinZoomFontSize = 6.0;
        private const double MaxZoomFontSize = 72.0;

        private IrcSwapchainHost _ircHost;
        private bool _updatingScroll;
        private bool _selecting;
        private double _viewportDips; // cached so MouseMove skips a per-move native call
        private int _wheelZoomRemainder; // accumulates sub-notch deltas (precision touchpads)
        private readonly DispatcherTimer _dragScrollTimer;

        // The persistent native renderer (ring buffer + input queue + parked
        // GPU surface), owned for the control's whole lifetime — scrollback
        // genuinely lives native-side and survives the control leaving and
        // re-entering the visual tree. Null only in the XAML designer. Freed
        // deterministically by Dispose, or by the SafeHandle finalizer as a
        // backstop.
        private readonly SafeRendererHandle _renderer;
        private bool _disposed;
        private bool _wrapExtendedColors = true; // mirrors the native default

        // LRU park registry (UI thread only — maintained from Loaded/Unloaded).
        // Parked controls keep their GPU surface warm for instant switch-back;
        // beyond ParkedViewLimit, the least-recently-hidden surface is
        // destroyed (scrollback always survives) and rebuilt in a few ms if
        // that window is revisited. Caps resident GPU cost at roughly
        // (1 + limit) x ~20-30 MB regardless of how many windows are open.
        private static readonly System.Collections.Generic.List<IrcChatControl> s_parkedViews = new System.Collections.Generic.List<IrcChatControl>();
        private static int s_parkedViewLimit = 2;

        // Every live (undisposed) control, for TrimAllMemory. UI thread only.
        private static readonly System.Collections.Generic.List<IrcChatControl> s_liveControls = new System.Collections.Generic.List<IrcChatControl>();

        /// <summary>How many hidden controls keep their native view parked
        /// (GPU surface resident) for instant reattach. Hidden controls beyond
        /// the limit have their surface destroyed — scrollback is unaffected —
        /// and rebuild in a few milliseconds when shown again. Default 2.
        /// UI thread only.</summary>
        public static int ParkedViewLimit
        {
            get => s_parkedViewLimit;
            set
            {
                s_parkedViewLimit = Math.Max(0, value);
                TrimParkedViews();
            }
        }

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

        /// <summary>Initializes the control and its persistent native
        /// scrollback. The rendering surface itself is created when the
        /// control is first loaded into a window, then parked (not destroyed)
        /// whenever the control leaves the visual tree.</summary>
        public IrcChatControl()
        {
            InitializeComponent();

            if (!DesignerProperties.GetIsInDesignMode(this))
            {
                // Fail with an actionable message instead of the opaque
                // BadImageFormat/DllNotFound the loader would produce.
                if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
                    throw new PlatformNotSupportedException(
                        "IrcChatControl.Wpf requires an x64 process (the native renderer ships as win-x64 only), " +
                        $"but this process is {RuntimeInformation.ProcessArchitecture}. Set <PlatformTarget>x64</PlatformTarget> " +
                        "in the host application project; on Windows ARM64 that runs the app under x64 emulation, which is supported.");

                _renderer = NativeMethods.CreateRenderer();
                if (_renderer.IsInvalid)
                    throw new InvalidOperationException("Failed to create native IRC renderer.");
                s_liveControls.Add(this);
            }

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
            s_parkedViews.Remove(this); // becoming visible: no longer parked
            RecreateOrResizeSurface();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            CancelSelectionDrag();
            if (_ircHost != null)
            {
                _ircHost.FrameRendered -= OnFrameRendered;
                ImageHost.Child = null; // DestroyWindowCore → DetachView (parks the surface)
                _ircHost.Dispose();
                _ircHost = null;

                // Track as most-recently-parked; evict the oldest surface(s)
                // over the limit so hidden windows don't pile up GPU stacks.
                if (!_disposed)
                {
                    s_parkedViews.Remove(this);
                    s_parkedViews.Add(this);
                    TrimParkedViews();
                }
            }
        }

        private static void TrimParkedViews()
        {
            while (s_parkedViews.Count > s_parkedViewLimit)
            {
                IrcChatControl oldest = s_parkedViews[0];
                s_parkedViews.RemoveAt(0);
                if (oldest._renderer != null && !oldest._renderer.IsClosed)
                    NativeMethods.ReleaseView(oldest._renderer);
            }
        }

        /// <summary>Deterministically frees the native renderer — scrollback,
        /// parked surface, and GPU stack — instead of waiting for GC
        /// finalization. Call when the chat window is closed for good; the
        /// control cannot be used afterward. UI thread only.</summary>
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            // Tear down the live view first so no render tick or producer
            // touches the handle mid-close.
            CancelSelectionDrag();
            if (_ircHost != null)
            {
                _ircHost.FrameRendered -= OnFrameRendered;
                ImageHost.Child = null;
                _ircHost.Dispose();
                _ircHost = null;
            }
            s_parkedViews.Remove(this);
            s_liveControls.Remove(this);
            _renderer?.Dispose(); // → DestroyRenderer (frees ring, arena, GPU)
        }

        /// <summary>Returns committed scrollback memory no longer in use to
        /// the OS (native arena compaction + decommit) — e.g. the high-water
        /// left behind by a message flood after eviction. Scrollback content
        /// is untouched. Cheap; intended for idle-time calls. UI thread only.</summary>
        public void TrimMemory()
        {
            if (RendererAvailable)
                NativeMethods.TrimStorage(_renderer);
        }

        /// <summary>Runs <see cref="TrimMemory"/> on every live control —
        /// hook this into an application idle/trim timer. UI thread only.</summary>
        public static void TrimAllMemory()
        {
            for (int i = 0; i < s_liveControls.Count; i++)
                s_liveControls[i].TrimMemory();
        }

        /// <summary>Appends one line to the scrollback. Thread-safe and cheap:
        /// any thread may call this at thousands of lines per second. mIRC
        /// control codes and ANSI SGR sequences are parsed inline; lines are
        /// capped at 512 UTF-8 bytes. The scrollback lives on the persistent
        /// native renderer, so lines added while the control is unloaded are
        /// ingested immediately and appear when it reloads — nothing is
        /// dropped or replayed.</summary>
        public void AddLine(string text)
        {
            if (string.IsNullOrEmpty(text) || _renderer == null || _renderer.IsClosed)
                return;

            // Encode on the stack: this runs thousands of times per second from
            // producer threads, and a heap byte[] per line just feeds the GC. The
            // native side caps lines at 512 bytes, so anything past that is dropped.
            Span<byte> buffer = stackalloc byte[512];
            System.Text.Unicode.Utf8.FromUtf16(text.AsSpan(), buffer,
                out _, out int bytesWritten);
            if (bytesWritten > 0)
            {
                try
                {
                    NativeMethods.AddLine(_renderer, ref MemoryMarshal.GetReference(buffer), bytesWritten);
                }
                catch (ObjectDisposedException)
                {
                    // A producer racing Dispose (window closed mid-flood):
                    // the line is for a dead window — drop it.
                    return;
                }
                _ircHost?.NotifyLinesPending(); // wake the attached view's timer, if any
            }
        }

        // False in the XAML designer and after Dispose. UI-thread members
        // check this instead of null alone — a disposed SafeHandle throws
        // ObjectDisposedException from P/Invoke marshaling otherwise.
        private bool RendererAvailable => _renderer != null && !_renderer.IsClosed;

        /// <summary>Removes every line from the scrollback and decommits the
        /// backing text arena. UI thread only.</summary>
        public void Clear()
        {
            if (!RendererAvailable)
                return;
            NativeMethods.Clear(_renderer);
            _ircHost?.Wake();
        }

        /// <summary>Number of lines currently held in the scrollback ring
        /// buffer (after any eviction by <see cref="SetMaxLines"/>). Stays
        /// meaningful while the control is unloaded — the scrollback is
        /// persistent. Zero after Dispose.</summary>
        public int LineCount => RendererAvailable ? NativeMethods.GetLineCount(_renderer) : 0;

        /// <summary>Scrolls to the newest line and re-pins auto-follow, so
        /// subsequent <see cref="AddLine"/> calls keep the view at the
        /// bottom.</summary>
        public void ScrollToEnd()
        {
            if (!RendererAvailable)
                return;
            NativeMethods.ScrollToEnd(_renderer);
            _ircHost?.Wake();
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

        /// <summary>Classic-client color compatibility (default true): inbound
        /// mIRC \x03 color indices 16–98 fold onto the basic 16-color palette
        /// (index mod 16), the way pre-extended-palette clients rendered
        /// rainbow spam and art. Set false to use the standardized extended
        /// palette (modern mIRC behavior). Applies to newly added lines.</summary>
        public bool WrapExtendedColors
        {
            get => _wrapExtendedColors;
            set
            {
                _wrapExtendedColors = value;
                if (RendererAvailable)
                    NativeMethods.SetExtendedColorWrap(_renderer, value);
            }
        }

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

        // The setters below write straight to the persistent renderer, which
        // retains every value across view park/unpark — no re-push on attach.

        private static void OnBackgroundChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var c = (IrcChatControl)d;
            if (c.ImageHost != null)
                c.ImageHost.Background = e.NewValue as Brush;
            if (e.NewValue is SolidColorBrush brush && c.RendererAvailable)
            {
                NativeMethods.SetBackgroundColor(c._renderer, PackArgb(brush.Color));
                c._ircHost?.Wake();
            }
        }

        private static void OnForegroundChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var c = (IrcChatControl)d;
            if (e.NewValue is SolidColorBrush brush && c.RendererAvailable)
            {
                NativeMethods.SetForegroundColor(c._renderer, PackArgb(brush.Color));
                c._ircHost?.Wake();
            }
        }

        private static void OnSelectionBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var c = (IrcChatControl)d;
            if (e.NewValue is SolidColorBrush brush && c.RendererAvailable)
            {
                NativeMethods.SetSelectionColor(c._renderer, PackArgba(brush.Color));
                c._ircHost?.Wake();
            }
        }

        private static void OnFontFamilyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var c = (IrcChatControl)d;
            string family = (e.NewValue as FontFamily)?.Source;
            if (!string.IsNullOrEmpty(family) && c.RendererAvailable)
            {
                NativeMethods.SetFontFamily(c._renderer, family);
                c._ircHost?.Wake();
            }
        }

        private static void OnFontSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var c = (IrcChatControl)d;
            double size = (double)e.NewValue;
            if (size > 0.0 && c.RendererAvailable)
            {
                NativeMethods.SetFontSize(c._renderer, (float)size);
                c._ircHost?.Wake();
            }
        }

        /// <summary>Sets the scrollback retention limit. Once reached, the
        /// oldest lines are evicted as new lines arrive at the bottom.
        /// Clamped natively to [1, 50000]; the 16 MiB text arena can evict
        /// earlier for extremely long lines. Shrinking below the current
        /// line count evicts immediately. Non-positive values are ignored.
        /// Safe to call whether or not the control is loaded.</summary>
        public void SetMaxLines(int maxLines)
        {
            if (maxLines <= 0 || !RendererAvailable)
                return;
            NativeMethods.SetMaxLines(_renderer, (uint)maxLines);
            _ircHost?.Wake();
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
            if (_renderer == null || _renderer.IsClosed || _disposed
                || !TryGetViewportPixels(out int px, out int py, out DpiScale dpi))
                return;

            if (_ircHost == null)
            {
                // First load creates the native view; every later load unparks
                // the surviving surface (SetParent + ShowWindow — the renderer
                // retains theme, font, max-lines, and all scrollback, so
                // nothing is re-pushed or replayed).
                _ircHost = new IrcSwapchainHost(_renderer, px, py, dpi.DpiScaleX);
                _ircHost.FrameRendered += OnFrameRendered;
                ImageHost.Child = _ircHost; // BuildWindowCore → AttachView
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

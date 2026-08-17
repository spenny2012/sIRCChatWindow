using System;
using System.Buffers;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Unicode;
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

        // Mirrors IrcLineTextSize in the native RingBuffer.h — the renderer's
        // per-line slot. Input longer than this is split across lines, never cut.
        private const int MaxLineBytes = 512;

        // Bounds on the style prefix re-applied to a continuation line:
        // "\x04RRGGBB,RRGGBB" is the longest color code, and an SGR sequence is
        // clipped to the second. Together with the five 1-byte toggles the
        // prefix cannot exceed 43 bytes, so it can never starve the payload.
        private const int MaxColorCodeBytes = 14;
        private const int MaxSgrBytes = 24;

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
        private bool _wrapExtendedColors = false; // mirrors the native default

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

        /// <summary>Appends text to the scrollback. Thread-safe and cheap: any
        /// thread may call this at thousands of lines per second. mIRC control
        /// codes and ANSI SGR sequences are parsed inline.
        /// <para>Input is never truncated. Embedded CR/LF start new lines, and
        /// text longer than the renderer's 512-byte line slot is split across
        /// continuation lines — broken at a word boundary where one is
        /// available, with the active color and style re-applied to each piece.
        /// One call can therefore add more than one line, so
        /// <see cref="LineCount"/> may rise by more than one.</para>
        /// <para>Note the slot is measured in raw UTF-8 bytes <i>including</i>
        /// control codes, which the parser strips only after ingest: every
        /// <c>\x04RRGGBB</c> spends 7 bytes of the line budget.</para>
        /// The scrollback lives on the persistent native renderer, so lines
        /// added while the control is unloaded are ingested immediately and
        /// appear when it reloads — nothing is dropped or replayed.</summary>
        public void AddLine(string text)
        {
            if (string.IsNullOrEmpty(text) || _renderer == null || _renderer.IsClosed)
                return;

            ReadOnlySpan<char> span = text.AsSpan();

            // Fast path: an ordinary short single line. Encodes on the stack —
            // this runs thousands of times per second from producer threads and
            // a heap byte[] per line just feeds the GC. Only input that actually
            // needs splitting pays for the slower path below.
            if (span.IndexOfAny('\r', '\n') < 0)
            {
                Span<byte> buffer = stackalloc byte[MaxLineBytes];
                if (Utf8.FromUtf16(span, buffer, out _, out int written) == OperationStatus.Done)
                {
                    if (written > 0 && Push(buffer.Slice(0, written)))
                        _ircHost?.NotifyLinesPending(); // wake the attached view's timer, if any
                    return;
                }
                // DestinationTooSmall — fall through and split it.
            }

            AddLineSplit(span);
        }

        // Hands one encoded line to the native queue. False means the renderer
        // was disposed underneath us (window closed mid-flood), so the line is
        // for a dead window and the caller should stop.
        private bool Push(Span<byte> utf8)
        {
            try
            {
                NativeMethods.AddLine(_renderer, ref MemoryMarshal.GetReference(utf8), utf8.Length);
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
            return true;
        }

        // Slow path: CR/LF become separate lines and any segment too long for
        // one slot is chunked. Wakes the view once at the end, not per chunk.
        private void AddLineSplit(ReadOnlySpan<char> text)
        {
            bool pushed = false;
            int start = 0;

            for (int i = 0; i <= text.Length; i++)
            {
                if (i < text.Length && text[i] != '\r' && text[i] != '\n')
                    continue;

                if (i > start)
                {
                    if (!PushSegment(text.Slice(start, i - start)))
                        return; // renderer gone
                    pushed = true;
                }

                if (i == text.Length)
                    break;

                if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    i++; // CRLF is one break, not two
                start = i + 1;
            }

            if (pushed)
                _ircHost?.NotifyLinesPending();
        }

        // Encodes one newline-free segment, then chunks it to the slot size.
        private bool PushSegment(ReadOnlySpan<char> segment)
        {
            byte[] rented = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(segment.Length));
            try
            {
                // The rental is sized for the worst case, so this always completes.
                Utf8.FromUtf16(segment, rented, out _, out int total);
                if (total == 0)
                    return true;
                return PushChunks(rented.AsSpan(0, total));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        // Splits an encoded segment across slots, re-applying the active style
        // to every continuation so colors survive the break. No text can be
        // lost here: `pos` advances by at least one byte per iteration and the
        // loop runs until the whole span is consumed.
        private bool PushChunks(Span<byte> bytes)
        {
            if (bytes.Length <= MaxLineBytes)
                return Push(bytes);

            Span<byte> chunk = stackalloc byte[MaxLineBytes];
            Span<byte> color = stackalloc byte[MaxColorCodeBytes];
            Span<byte> sgr = stackalloc byte[MaxSgrBytes];
            int colorLen = 0;
            int sgrLen = 0;
            byte toggles = 0;
            bool first = true;
            int pos = 0;

            while (pos < bytes.Length)
            {
                int prefixLen = first
                    ? 0
                    : WriteStylePrefix(chunk, toggles, color.Slice(0, colorLen), sgr.Slice(0, sgrLen));
                int budget = MaxLineBytes - prefixLen;

                ReadOnlySpan<byte> rest = bytes.Slice(pos);
                int take = rest.Length <= budget ? rest.Length : FindSplit(rest, budget);

                rest.Slice(0, take).CopyTo(chunk.Slice(prefixLen));
                if (!Push(chunk.Slice(0, prefixLen + take)))
                    return false;

                AdvanceStyle(rest.Slice(0, take), ref toggles, color, ref colorLen, sgr, ref sgrLen);

                pos += take;
                if (pos < bytes.Length && bytes[pos] == (byte)' ')
                    pos++; // swallow the space we broke on
                first = false;
            }

            return true;
        }

        // Largest prefix of `s` fitting in `budget` bytes without cutting a
        // UTF-8 scalar or a control sequence, preferring the last space in the
        // final quarter — unlike the renderer's own width-adaptive word wrap,
        // this break is baked in at ingest. Always returns at least 1 so the
        // caller keeps making progress.
        private static int FindSplit(ReadOnlySpan<byte> s, int budget)
        {
            int safe = 0;  // last boundary that is not inside a sequence
            int space = 0; // last boundary just past a space
            int i = 0;

            while (i < budget)
            {
                int seq = SequenceLength(s, i);
                if (seq > 0)
                {
                    if (i + seq > budget)
                        break; // the sequence itself would be cut
                    i += seq;
                    safe = i;
                    continue;
                }

                int ch = Utf8SequenceLength(s[i]);
                if (i + ch > budget)
                    break; // the scalar would be cut
                bool wasSpace = s[i] == (byte)' ';
                i += ch;
                safe = i;
                if (wasSpace)
                    space = i;
            }

            if (space > 0 && space >= budget - budget / 4)
                return space;
            if (safe > 0)
                return safe;

            // One sequence or scalar longer than the entire budget. Cut at the
            // largest scalar boundary that fits so the loop still advances.
            int hard = budget;
            while (hard > 1 && (s[hard] & 0xC0) == 0x80)
                hard--;
            return hard;
        }

        // Replays the style effects of an emitted chunk so the next
        // continuation can re-apply them.
        private static void AdvanceStyle(ReadOnlySpan<byte> s, ref byte toggles,
            Span<byte> color, ref int colorLen, Span<byte> sgr, ref int sgrLen)
        {
            int i = 0;
            while (i < s.Length)
            {
                int seq = SequenceLength(s, i);
                if (seq == 0)
                {
                    i += Utf8SequenceLength(s[i]);
                    continue;
                }

                switch (s[i])
                {
                    case 0x02: toggles ^= 0x01; break; // bold
                    case 0x1D: toggles ^= 0x02; break; // italic
                    case 0x1F: toggles ^= 0x04; break; // underline
                    case 0x1E: toggles ^= 0x08; break; // strikethrough
                    case 0x16: toggles ^= 0x10; break; // reverse
                    case 0x0F:                          // reset everything
                        toggles = 0;
                        colorLen = 0;
                        sgrLen = 0;
                        break;
                    case 0x03:
                    case 0x04:
                        // Carried verbatim: re-emitting the original bytes
                        // reproduces the exact color, bare-reset forms included.
                        colorLen = seq <= color.Length ? seq : 0;
                        if (colorLen > 0)
                            s.Slice(i, colorLen).CopyTo(color);
                        break;
                    case 0x1B:
                        // Only SGR changes style; other CSI sequences are inert.
                        if (seq <= sgr.Length && s[i + seq - 1] == (byte)'m')
                        {
                            sgrLen = seq;
                            s.Slice(i, seq).CopyTo(sgr);
                        }
                        break;
                }
                i += seq;
            }
        }

        // Writes the carried style to the front of `dest` and returns its
        // length. Toggles lead so a following color code is not undone by them.
        private static int WriteStylePrefix(Span<byte> dest, byte toggles,
            ReadOnlySpan<byte> color, ReadOnlySpan<byte> sgr)
        {
            int n = 0;
            if ((toggles & 0x01) != 0) dest[n++] = 0x02;
            if ((toggles & 0x02) != 0) dest[n++] = 0x1D;
            if ((toggles & 0x04) != 0) dest[n++] = 0x1F;
            if ((toggles & 0x08) != 0) dest[n++] = 0x1E;
            if ((toggles & 0x10) != 0) dest[n++] = 0x16;
            if (color.Length > 0) { color.CopyTo(dest.Slice(n)); n += color.Length; }
            if (sgr.Length > 0) { sgr.CopyTo(dest.Slice(n)); n += sgr.Length; }
            return n;
        }

        // Byte length of the control sequence starting at s[i], or 0 when s[i]
        // begins ordinary text. Mirrors IrcParser::Parse in the native renderer
        // (src/IrcRendererNative/IrcParser.cpp). Only chunk cosmetics depend on
        // this agreeing exactly — no text is lost if it does not, because
        // chunking advances through every byte regardless.
        private static int SequenceLength(ReadOnlySpan<byte> s, int i)
        {
            switch (s[i])
            {
                case 0x01: // CTCP delimiter (stripped by the parser)
                case 0x02: // bold
                case 0x0F: // reset
                case 0x11: // monospace toggle (stripped)
                case 0x16: // reverse
                case 0x1D: // italic
                case 0x1E: // strikethrough
                case 0x1F: // underline
                    return 1;
                case 0x03:
                    return 1 + MircColorLength(s, i + 1);
                case 0x04:
                    return 1 + HexColorLength(s, i + 1);
                case 0x1B:
                    return EscapeLength(s, i);
                default:
                    return 0;
            }
        }

        // \x03 payload: up to two foreground digits, then ",NN" only when a
        // digit follows the comma (otherwise the comma is literal text).
        private static int MircColorLength(ReadOnlySpan<byte> s, int p)
        {
            int start = p;
            p = SkipDigits(s, p, 2);
            if (p + 1 < s.Length && s[p] == (byte)',' && IsDigit(s[p + 1]))
                p = SkipDigits(s, p + 1, 2);
            return p - start;
        }

        // \x04 payload: exactly six hex digits or nothing is consumed; the
        // background half likewise needs its own valid six.
        private static int HexColorLength(ReadOnlySpan<byte> s, int p)
        {
            int start = p;
            if (!IsHex6(s, p))
                return 0;
            p += 6;
            if (p < s.Length && s[p] == (byte)',' && IsHex6(s, p + 1))
                p += 7;
            return p - start;
        }

        // ESC sequences: OSC runs to BEL, CSI to its final byte, and the
        // remaining forms are two bytes (three for charset selects).
        private static int EscapeLength(ReadOnlySpan<byte> s, int i)
        {
            int p = i + 1;
            if (p >= s.Length)
                return 1; // bare ESC at end of line

            byte c = s[p];
            if (c == (byte)']')
            {
                while (p < s.Length && s[p] != 0x07) p++;
                if (p < s.Length) p++;
                return p - i;
            }
            if (c != (byte)'[')
            {
                p++;
                if ((c == (byte)'(' || c == (byte)')' || c == (byte)'#') && p < s.Length) p++;
                return p - i;
            }

            p++; // past '['
            while (p < s.Length)
            {
                byte b = s[p];
                if (b >= 0x20 && b <= 0x3F) { p++; continue; } // parameter/intermediate
                if (b >= 0x40 && b <= 0x7E) { p++; break; }    // final byte
                break;                                          // control/high byte: abandoned
            }
            return p - i;
        }

        private static int Utf8SequenceLength(byte b)
        {
            if (b < 0x80) return 1;
            if ((b & 0xE0) == 0xC0) return 2;
            if ((b & 0xF0) == 0xE0) return 3;
            if ((b & 0xF8) == 0xF0) return 4;
            return 1; // stray continuation or invalid lead: its own unit
        }

        private static bool IsDigit(byte b)
        {
            return b >= (byte)'0' && b <= (byte)'9';
        }

        private static int SkipDigits(ReadOnlySpan<byte> s, int p, int max)
        {
            int n = 0;
            while (p < s.Length && n < max && IsDigit(s[p])) { p++; n++; }
            return p;
        }

        private static bool IsHex6(ReadOnlySpan<byte> s, int p)
        {
            if (p < 0 || p + 6 > s.Length)
                return false;
            for (int k = 0; k < 6; k++)
            {
                byte b = s[p + k];
                if (!((b >= (byte)'0' && b <= (byte)'9')
                   || (b >= (byte)'a' && b <= (byte)'f')
                   || (b >= (byte)'A' && b <= (byte)'F')))
                    return false;
            }
            return true;
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

        /// <summary>Classic-client color compatibility (default false): inbound
        /// mIRC \x03 color indices 16–98 render using the standardized extended
        /// palette (modern mIRC behavior). Set true to fold them onto the basic
        /// 16-color palette (index mod 16) instead, the way pre-extended-palette
        /// clients rendered rainbow spam and art. Applies to newly added lines.</summary>
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

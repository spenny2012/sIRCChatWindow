using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Media;

namespace IrcChatWpf
{
    public partial class MainWindow : Window
    {
        private const string Esc = "";

        private Thread _workerThread;
        private bool _running;
        private readonly Stopwatch _stopwatch = new Stopwatch();
        private readonly Random _random = new Random();
        private readonly StringBuilder _sb = new StringBuilder(512);
        private readonly Action _updateStatsAction;

        public MainWindow()
        {
            InitializeComponent();
            Closed += OnClosed;
            _updateStatsAction = UpdateStats;
        }

        private void OnClosed(object sender, EventArgs e)
        {
            _running = false;
            _workerThread?.Join(500);
        }

        private void OnStartClick(object sender, RoutedEventArgs e)
        {
            if (_running)
                return;

            if (!int.TryParse(RateTextBox.Text, out int rate) || rate <= 0)
                rate = 2000;

            _running = true;
            _stopwatch.Restart();
            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;

            _workerThread = new Thread(() => WorkerLoop(rate))
            {
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal
            };
            _workerThread.Start();
        }

        private void OnStopClick(object sender, RoutedEventArgs e)
        {
            _running = false;
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
        }

        private void OnClearClick(object sender, RoutedEventArgs e)
        {
            ChatControl.Clear();
        }

        // Deterministic formatting showcase: mIRC classic/extended colors,
        // ANSI SGR colors (basic/256/truecolor), attributes, and malformed
        // escape sequences that must be stripped without residue. Rows stay
        // within the native 16-segments-per-line cap (label + <=15 swatches).
        private void OnTestPatternClick(object sender, RoutedEventArgs e)
        {
            var sb = new StringBuilder(512);

            ChatControl.AddLine("=== mIRC classic 0-15 ===");
            for (int row = 0; row < 2; ++row)
            {
                sb.Clear();
                int start = row * 8;
                sb.Append($"{start:D2}-{start + 7:D2}: ");
                for (int n = start; n < start + 8; ++n)
                    sb.Append('').Append(n.ToString("D2")).Append("##");
                sb.Append('');
                ChatControl.AddLine(sb.ToString());
            }
            ChatControl.AddLine("0,04white-on-red 8,02yellow-on-navy 99default-99 plain");

            ChatControl.AddLine("=== mIRC extended 16-98 ===");
            for (int start = 16; start <= 98; start += 14)
            {
                sb.Clear();
                int end = Math.Min(start + 13, 98);
                sb.Append($"{start:D2}-{end:D2}: ");
                for (int n = start; n <= end; ++n)
                    sb.Append('').Append(n.ToString("D2")).Append("##");
                sb.Append('');
                ChatControl.AddLine(sb.ToString());
            }

            ChatControl.AddLine("=== ANSI basic ===");
            sb.Clear();
            sb.Append("fg 30-37:   ");
            for (int n = 30; n <= 37; ++n)
                sb.Append(Esc).Append('[').Append(n).Append("m##");
            ChatControl.AddLine(sb.Append(Esc).Append("[0m").ToString());
            sb.Clear();
            sb.Append("fg 90-97:   ");
            for (int n = 90; n <= 97; ++n)
                sb.Append(Esc).Append('[').Append(n).Append("m##");
            ChatControl.AddLine(sb.Append(Esc).Append("[0m").ToString());
            sb.Clear();
            sb.Append("bg 40-47:   ");
            for (int n = 40; n <= 47; ++n)
                sb.Append(Esc).Append('[').Append(n).Append("m  ");
            ChatControl.AddLine(sb.Append(Esc).Append("[0m").ToString());
            sb.Clear();
            sb.Append("bg 100-107: ");
            for (int n = 100; n <= 107; ++n)
                sb.Append(Esc).Append('[').Append(n).Append("m  ");
            ChatControl.AddLine(sb.Append(Esc).Append("[0m").ToString());

            ChatControl.AddLine($"bold-brighten: {Esc}[31mdark {Esc}[0m{Esc}[1;31mbright {Esc}[0m{Esc}[31;1malso-bright{Esc}[0m {Esc}[1;31mSGR22:{Esc}[22mdark-again{Esc}[0m");

            ChatControl.AddLine("=== xterm 256 ===");
            sb.Clear();
            sb.Append("cube:      ");
            for (int k = 0; k < 12; ++k)
                sb.Append(Esc).Append("[38;5;").Append(16 + 18 * k).Append("m##");
            ChatControl.AddLine(sb.Append(Esc).Append("[0m").ToString());
            sb.Clear();
            sb.Append("gray fg A: ");
            for (int n = 232; n <= 243; ++n)
                sb.Append(Esc).Append("[38;5;").Append(n).Append("m##");
            ChatControl.AddLine(sb.Append(Esc).Append("[0m").ToString());
            sb.Clear();
            sb.Append("gray fg B: ");
            for (int n = 244; n <= 255; ++n)
                sb.Append(Esc).Append("[38;5;").Append(n).Append("m##");
            ChatControl.AddLine(sb.Append(Esc).Append("[0m").ToString());
            sb.Clear();
            sb.Append("bg 48;5:   ");
            for (int k = 0; k < 12; ++k)
                sb.Append(Esc).Append("[48;5;").Append(21 + 36 * (k % 6) + 6 * (k / 2)).Append("m  ");
            ChatControl.AddLine(sb.Append(Esc).Append("[0m").ToString());

            sb.Clear();
            sb.Append("truecolor: ");
            for (int k = 0; k < 14; ++k)
            {
                int v = k * 255 / 13;
                sb.Append(Esc).Append("[38;2;").Append(v).Append(";0;").Append(255 - v).Append("m#");
            }
            ChatControl.AddLine(sb.Append(Esc).Append("[0m").ToString());

            ChatControl.AddLine($"attrs: {Esc}[3mitalic{Esc}[23m {Esc}[4munderline{Esc}[24m {Esc}[7mreverse{Esc}[27m {Esc}[1mbold{Esc}[22m plain");
            ChatControl.AddLine($"mixed: 04mirc-red {Esc}[32mansi-green +bold{Esc}[0m done plain");

            ChatControl.AddLine("=== malformed (must show clean text, no residue) ===");
            ChatControl.AddLine($"inline: A{Esc}[999mB {Esc}[38;5mC {Esc}[2JD {Esc}]0;osc-titleE");
            ChatControl.AddLine($"truncated CSI at EOL (line should end at the colon): {Esc}[12;");
            ChatControl.AddLine($"bare ESC at EOL: ok{Esc}");

            // Emoji/CJK cell-model checks. Escapes only: this file has no BOM
            // and already carries raw control bytes, so keep it ASCII-safe.
            ChatControl.AddLine("=== emoji ===");
            // Grid ruler: on the line below it, c must land under '4', e
            // under '8', g under the '2' of "12" if 2-cell emoji are exact.
            ChatControl.AddLine("ruler: 0123456789012345678901234567890123456789");
            ChatControl.AddLine("ruler: ab\U0001F600cd\U0001F601ef\U0001F602gh end-grid");
            ChatControl.AddLine("smileys: \U0001F600 \U0001F601 \U0001F602 \U0001F923 \U0001F60D end");
            ChatControl.AddLine("3-byte: heart-text ❤ heart-vs16 ❤️ sun ☀ check ✔️ star ⭐ end");
            ChatControl.AddLine("flags: US \U0001F1FA\U0001F1F8 JP \U0001F1EF\U0001F1F5 lone-RI \U0001F1E6 end");
            ChatControl.AddLine("skin: \U0001F44D \U0001F44D\U0001F3FD \U0001F44D\U0001F3FF end");
            ChatControl.AddLine("zwj: family \U0001F468‍\U0001F469‍\U0001F467 rainbow-flag \U0001F3F3️‍\U0001F308 end");
            ChatControl.AddLine("keycap: 1️⃣ combining: é (e + U+0301) end");
            ChatControl.AddLine("cjk: ab你好cd日本語ef한국어gh end");
            ChatControl.AddLine("mixed-mirc: 04red \U0001F600 red plain 08,02\U0001F31F on-navy end");
            ChatControl.AddLine($"mixed-ansi: {Esc}[32mgreen \U0001F600 green{Esc}[0m {Esc}[1;35m\U0001F49C bold-purple{Esc}[0m end");
            ChatControl.AddLine($"csi-abandon: {Esc}[38;5;\U0001F600 must show emoji, no residue");
            sb.Clear();
            sb.Append("wrap-test: ");
            for (int k = 0; k < 30; ++k)
                sb.Append("\U0001F600\U0001F389❤️ ");
            ChatControl.AddLine(sb.ToString()); // ~461 UTF-8 bytes: wraps, breaks at cluster starts
        }

        private bool _lightTheme;

        private void OnThemeClick(object sender, RoutedEventArgs e)
        {
            _lightTheme = !_lightTheme;
            if (_lightTheme)
            {
                ChatControl.SetBackgroundColor(Color.FromRgb(0xFA, 0xFA, 0xFA));
                ChatControl.SetForegroundColor(Color.FromRgb(0x1A, 0x1A, 0x1A));
                ChatControl.SetSelectionColor(Color.FromArgb(0x59, 0xFF, 0x8C, 0x00)); // translucent amber
            }
            else
            {
                ChatControl.SetBackgroundColor(Color.FromRgb(0x14, 0x14, 0x14));
                ChatControl.SetForegroundColor(Color.FromRgb(0xF2, 0xF2, 0xF2));
                ChatControl.SetSelectionColor(Color.FromArgb(0x59, 0x59, 0x8C, 0xF2)); // translucent blue (default)
            }
            // Reverse span bakes the theme current at parse time — this line
            // should swap against the theme just applied.
            ChatControl.AddLine($"theme now {(_lightTheme ? "light" : "dark")}: {Esc}[7mreverse{Esc}[27m plain");
        }

        private int _fontPreset;

        // Cycles: default monospace -> larger monospace -> non-monospace (to
        // visually demonstrate the accepted uneven-spacing limitation).
        private void OnFontClick(object sender, RoutedEventArgs e)
        {
            _fontPreset = (_fontPreset + 1) % 3;
            string family;
            double size;
            switch (_fontPreset)
            {
                case 1: family = "Cascadia Mono"; size = 18; break;
                case 2: family = "Times New Roman"; size = 20; break;
                default: family = "Consolas"; size = 14; break;
            }
            ChatControl.SetFontFamily(family);
            ChatControl.SetFontSize(size);
            ChatControl.AddLine($"font now: {family} {size}pt");
        }

        private bool _capped;

        private void OnCapClick(object sender, RoutedEventArgs e)
        {
            _capped = !_capped;
            ChatControl.SetMaxLines(_capped ? 100 : 50000);
            CapButton.Content = _capped ? "Uncap" : "Cap 100";
        }

        private void WorkerLoop(int targetRate)
        {
            var sw = Stopwatch.StartNew();
            long linesGenerated = 0;

            while (_running)
            {
                string line = GenerateRandomIrcLine();
                ChatControl.AddLine(line);
                linesGenerated++;

                double expectedTime = (double)linesGenerated / targetRate;
                double elapsed = sw.Elapsed.TotalSeconds;
                if (expectedTime > elapsed)
                {
                    int sleepMs = (int)((expectedTime - elapsed) * 1000);
                    if (sleepMs > 0)
                        Thread.Sleep(sleepMs);
                }

                if (linesGenerated % 100 == 0)
                {
                    Dispatcher.BeginInvoke(_updateStatsAction);
                }
            }
        }

        private string GenerateRandomIrcLine()
        {
            _sb.Clear();

            // Randomly prefix with formatting.
            if (_random.Next(4) == 0) _sb.Append('\u0002'); // bold
            if (_random.Next(4) == 0) _sb.Append("\u0003").Append(_random.Next(100)); // color (incl. extended + 99)

            _sb.Append("[")
               .Append(_random.Next(10000).ToString("D4"))
               .Append("] ")
               .Append("User")
               .Append(_random.Next(100))
               .Append(": ");

            int words = _random.Next(5, 25);
            for (int i = 0; i < words; ++i)
            {
                if (_random.Next(8) == 0)
                {
                    _sb.Append("\u0003").Append(_random.Next(100));
                    if (_random.Next(2) == 0)
                        _sb.Append(",").Append(_random.Next(100));
                }
                if (_random.Next(10) == 0) _sb.Append('\u0002');
                if (_random.Next(12) == 0) _sb.Append('\u001F');
                if (_random.Next(14) == 0) _sb.Append('\u0016');

                if (_random.Next(8) == 0)
                {
                    switch (_random.Next(6))
                    {
                        case 0: _sb.Append(Esc).Append('[').Append(30 + _random.Next(8)).Append('m'); break;
                        case 1: _sb.Append(Esc).Append('[').Append(90 + _random.Next(8)).Append('m'); break;
                        case 2: _sb.Append(Esc).Append("[1;3").Append(_random.Next(8)).Append('m'); break;
                        case 3: _sb.Append(Esc).Append("[38;5;").Append(_random.Next(256)).Append('m'); break;
                        case 4: _sb.Append(Esc).Append("[38;2;").Append(_random.Next(256)).Append(';')
                                   .Append(_random.Next(256)).Append(';').Append(_random.Next(256)).Append('m'); break;
                        case 5: _sb.Append(Esc).Append("[4").Append(_random.Next(8)).Append('m'); break;
                    }
                }
                if (_random.Next(10) == 0) _sb.Append(Esc).Append("[0m");
                // Visual fuzzing: self-terminating malformed sequences; any
                // rendered "[999m"-style residue signals a stripping bug.
                if (_random.Next(40) == 0)
                {
                    switch (_random.Next(4))
                    {
                        case 0: _sb.Append(Esc).Append("[999m"); break;
                        case 1: _sb.Append(Esc).Append("[38;5m"); break;
                        case 2: _sb.Append(Esc).Append("[2J"); break;
                        case 3: _sb.Append(Esc).Append("[;m"); break;
                    }
                }

                _sb.Append("word").Append(_random.Next(1000));
                if (i < words - 1)
                    _sb.Append(' ');
            }

            if (_random.Next(4) == 0)
                _sb.Append('\u000F');

            return _sb.ToString();
        }

        private void UpdateStats()
        {
            long count = ChatControl.LineCount;
            double seconds = _stopwatch.Elapsed.TotalSeconds;
            double rate = seconds > 0 ? count / seconds : 0;
            StatsText.Text = $"Lines: {count:N0}  Rate: {rate:N0}/s";
        }
    }
}

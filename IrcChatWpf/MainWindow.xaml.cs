using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Windows;

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
        public MainWindow()
        {
            InitializeComponent();
            Closed += OnClosed;
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
                    Dispatcher.BeginInvoke(new Action(UpdateStats));
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

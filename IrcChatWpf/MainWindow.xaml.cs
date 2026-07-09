using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Windows;

namespace IrcChatWpf
{
    public partial class MainWindow : Window
    {
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
            if (_random.Next(4) == 0) _sb.Append("\u0003").Append(_random.Next(16)); // color

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
                    _sb.Append("\u0003").Append(_random.Next(16));
                    if (_random.Next(2) == 0)
                        _sb.Append(",").Append(_random.Next(16));
                }
                if (_random.Next(10) == 0) _sb.Append('\u0002');
                if (_random.Next(12) == 0) _sb.Append('\u001F');
                if (_random.Next(14) == 0) _sb.Append('\u0016');

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

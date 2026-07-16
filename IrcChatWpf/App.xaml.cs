using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace IrcChatWpf
{
    public partial class App : Application
    {
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        private static extern bool SetProcessWorkingSetSize(IntPtr handle, IntPtr min, IntPtr max);

        public App()
        {
            // Keep WPF off the GPU: the chat surface renders through its own
            // D3D11 swapchain (HwndHost child), so WPF's hardware pipeline
            // would exist only to composite the trivial chrome — at the cost
            // of a D3D9Ex device, its composition surfaces, and the vendor
            // D3D9 driver stack (~tens of MB private). Recommended for any
            // app embedding IrcChatControl unless it has GPU-heavy WPF UI of
            // its own. Must run before the first window is created.
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        }

        private long _lastAllocated;
        private long _trimmedAtAllocated;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // One-shot trim once startup has settled: XAML/BAML parsing leaves
            // sizable garbage in committed GC regions that steady state never
            // touches again (chat lines are stackalloc-encoded straight into
            // the native queue). Aggressive collect decommits those regions;
            // the working-set trim then drops the resident number — with the
            // render timer parked at idle, almost nothing faults back in.
            var startupTrim = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            startupTrim.Tick += (sender, _) =>
            {
                ((DispatcherTimer)sender).Stop();
                Trim();
            };
            startupTrim.Start();

            // Idle re-trim: an activity burst (message flood) leaves garbage
            // in committed GC regions, and with zero steady-state allocation
            // no collection ever fires to release it. Once allocation has been
            // quiet for a full interval, trim once; new activity re-arms.
            _lastAllocated = GC.GetTotalAllocatedBytes();
            var idleTrim = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            idleTrim.Tick += (_, __) =>
            {
                long allocated = GC.GetTotalAllocatedBytes();
                bool quiet = allocated - _lastAllocated < 1024 * 1024;
                _lastAllocated = allocated;
                // The activity threshold (not exact equality) keeps the WPF
                // timer machinery's own few bytes per tick from re-triggering.
                if (quiet && allocated - _trimmedAtAllocated > 4 * 1024 * 1024)
                {
                    _trimmedAtAllocated = allocated;
                    Trim();
                }
            };
            idleTrim.Start();
        }

        private static void Trim()
        {
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            SetProcessWorkingSetSize(GetCurrentProcess(), (IntPtr)(-1), (IntPtr)(-1));
        }
    }
}

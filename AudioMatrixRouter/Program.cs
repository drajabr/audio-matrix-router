namespace AudioMatrixRouter;

static class Program
{
    private const string SingleInstanceMutexName = "AudioMatrixRouter.SingleInstance";
    private const string ShowWindowEventName    = "AudioMatrixRouter.ShowWindow";

    [System.Runtime.InteropServices.DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint timeBeginPeriod(uint uPeriod);

    [System.Runtime.InteropServices.DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint timeEndPeriod(uint uPeriod);

    [STAThread]
    static void Main(string[] args)
    {
        // MUST be the first statement: handles Velopack's --veloapp-* install/update/
        // uninstall invocations, which exit inside Run() and never reach the mutex below.
        // A post-update restart only happens after this process exits, so the
        // single-instance mutex is already released by then.
        Velopack.VelopackApp.Build().Run();

        using var mutex = new System.Threading.Mutex(true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            // Only signal the already-running instance to show itself when the user
            // explicitly launched the app (not from a --startup / --minimized shortcut,
            // which fires at boot when the app is already in the tray).
            bool isStartupLaunch = args.Any(arg =>
                string.Equals(arg, "--startup", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "--minimized", StringComparison.OrdinalIgnoreCase));

            if (!isStartupLaunch)
            {
                try
                {
                    using var ev = System.Threading.EventWaitHandle.OpenExisting(ShowWindowEventName);
                    ev.Set();
                }
                catch { /* instance may have just exited; ignore */ }
            }
            return;
        }

        var startMinimized = args.Any(arg =>
            string.Equals(arg, "--startup", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "--minimized", StringComparison.OrdinalIgnoreCase));

        // Create the named event so a second instance can signal us.
        using var showEvent = new System.Threading.EventWaitHandle(
            false,
            System.Threading.EventResetMode.AutoReset,
            ShowWindowEventName);

        timeBeginPeriod(1);
        try
        {
            System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
            try
            {
                // High, not RealTime: RealTime for the whole process (WebView2 UI, JSON
                // serialization, timers) starves the audio service and drivers — at boot
                // it made glitching WORSE. WASAPI event threads already get elevated by
                // the audio stack; High keeps the app responsive without fighting it.
                var process = System.Diagnostics.Process.GetCurrentProcess();
                process.PriorityClass = System.Diagnostics.ProcessPriorityClass.High;
            }
            catch
            {
                // Priority elevation can fail in constrained environments; continue safely.
            }

            ApplicationConfiguration.Initialize();
            var form = new MainForm(startMinimized);

            // Background thread: wait for second-instance signals and restore the window.
            var showThread = new System.Threading.Thread(() =>
            {
                while (!form.IsDisposed)
                {
                    if (showEvent.WaitOne(500) && !form.IsDisposed)
                    {
                        form.BeginInvoke(form.ShowFromSecondInstance);
                    }
                }
            })
            {
                IsBackground = true,
                Name = "ShowWindowListener"
            };
            showThread.Start();

            Application.Run(form);
        }
        finally
        {
            timeEndPeriod(1);
        }
    }
}

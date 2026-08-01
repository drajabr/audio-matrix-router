using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Threading;

namespace AudioMatrixRouter.App;

internal static class Program
{
    private const string SingleInstanceMutexName = "AudioMatrixRouter.SingleInstance";
    private const string ShowWindowEventName = "AudioMatrixRouter.ShowWindow";

    /// <summary>Launched with --startup / --minimized: open hidden in the tray.</summary>
    internal static bool StartMinimized { get; private set; }

    private static volatile bool _exited;

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint timeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint timeEndPeriod(uint uPeriod);

    [STAThread]
    public static void Main(string[] args)
    {
        // MUST be the first statement: handles Velopack's --veloapp-* install/update/
        // uninstall invocations, which exit inside Run() and never reach the mutex below.
        // A post-update restart only happens after this process exits, so the
        // single-instance mutex is already released by then.
        Velopack.VelopackApp.Build().Run();

        using var mutex = new Mutex(true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            // Only signal the already-running instance to show itself when the user
            // explicitly launched the app (not from a --startup / --minimized shortcut,
            // which fires at boot when the app is already in the tray).
            if (!IsStartupLaunch(args))
            {
                try
                {
                    using var ev = EventWaitHandle.OpenExisting(ShowWindowEventName);
                    ev.Set();
                }
                catch
                {
                    // Instance may have just exited; ignore.
                }
            }
            return;
        }

        StartMinimized = IsStartupLaunch(args);

        // Create the named event so a second instance can signal us.
        using var showEvent = new EventWaitHandle(
            false,
            EventResetMode.AutoReset,
            ShowWindowEventName);

        timeBeginPeriod(1);
        try
        {
            GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
            try
            {
                // High, not RealTime: RealTime for the whole process starves the audio
                // service and drivers — at boot it made glitching WORSE. WASAPI event
                // threads already get elevated by the audio stack; High keeps the app
                // responsive without fighting it.
                Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;
            }
            catch
            {
                // Priority elevation can fail in constrained environments; continue safely.
            }

            // Background thread: wait for second-instance signals and restore the window.
            var showThread = new Thread(() =>
            {
                while (!_exited)
                {
                    if (showEvent.WaitOne(500) && !_exited)
                    {
                        Dispatcher.UIThread.Post(() =>
                            (Application.Current as App)?.ShowMainWindow());
                    }
                }
            })
            {
                IsBackground = true,
                Name = "ShowWindowListener",
            };
            showThread.Start();

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            _exited = true;
            timeEndPeriod(1);
        }
    }

    private static bool IsStartupLaunch(string[] args) => args.Any(arg =>
        string.Equals(arg, "--startup", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(arg, "--minimized", StringComparison.OrdinalIgnoreCase));

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}

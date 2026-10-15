using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using AudioMatrixRouter.Models;
using Avalonia;
using Avalonia.Threading;

namespace AudioMatrixRouter.App;

/// <summary>
/// Keep-alive. The app spawns a second instance of itself in watchdog mode; that
/// instance holds its own mutex (so there is never more than one), polls the real
/// process and the memory-mapped heartbeat the engine health tick writes, and
/// relaunches the app with --startup when it crashes (non-zero exit) or hangs
/// (heartbeat older than 10 s). A clean exit (code 0) ends the watchdog. Crash-loop
/// guard: three relaunches within five minutes pause it for five minutes.
/// </summary>
internal static class Watchdog
{
    private const string MutexName = "AudioMatrixRouter.Watchdog";
    private const int PollMs = 2000;
    private const int LaunchGraceMs = 30_000;
    private const int LoopWindowMs = 300_000;
    private const int LoopMaxRelaunches = 3;

    public static void Spawn()
    {
        try
        {
            if (Mutex.TryOpenExisting(MutexName, out var existing))
            {
                existing.Dispose();
                return; // one is already watching
            }
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            Process.Start(new ProcessStartInfo(exe, $"--watchdog {Environment.ProcessId}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            EngineLog.Write("[Host] watchdog spawned");
        }
        catch (Exception ex)
        {
            EngineLog.Write($"[Host] watchdog spawn failed: {ex.Message}");
        }
    }

    public static void Run(int pid)
    {
        using var mutex = new Mutex(true, MutexName, out var created);
        if (!created) return;

        var exe = Environment.ProcessPath ?? "";
        Process? target = null;
        try { target = Process.GetProcessById(pid); } catch { }
        long launchedAt = Environment.TickCount64;
        var relaunches = new Queue<long>();
        EngineLog.Write($"[Watchdog] watching pid {pid}");

        while (true)
        {
            Thread.Sleep(PollMs);
            long now = Environment.TickCount64;

            if (target is null || target.HasExited)
            {
                int code = -1;
                try { code = target?.ExitCode ?? -1; } catch { }
                if (code == 0)
                {
                    EngineLog.Write("[Watchdog] app exited cleanly — done");
                    return;
                }
                target = Relaunch(exe, relaunches, $"exit code {code}");
                launchedAt = Environment.TickCount64;
                continue;
            }

            if (now - launchedAt < LaunchGraceMs) continue;
            long age = ProcessHeartbeat.ReadAgeMs();
            if (age >= 0 && age <= ProcessHeartbeat.StaleAfterMs) continue;

            EngineLog.Write($"[Watchdog] heartbeat {(age < 0 ? "missing" : age + " ms old")} — killing pid {target.Id}");
            try { target.Kill(entireProcessTree: true); target.WaitForExit(5000); } catch { }
            target = Relaunch(exe, relaunches, "heartbeat stale");
            launchedAt = Environment.TickCount64;
        }
    }

    private static Process? Relaunch(string exe, Queue<long> relaunches, string reason)
    {
        long now = Environment.TickCount64;
        while (relaunches.Count > 0 && now - relaunches.Peek() > LoopWindowMs) relaunches.Dequeue();
        if (relaunches.Count >= LoopMaxRelaunches)
        {
            EngineLog.Write($"[Watchdog] {LoopMaxRelaunches} relaunches in 5 min — pausing 5 min before trying again");
            Thread.Sleep(LoopWindowMs);
            relaunches.Clear();
        }
        relaunches.Enqueue(Environment.TickCount64);
        EngineLog.Write($"[Watchdog] relaunching app ({reason})");
        try
        {
            return Process.Start(new ProcessStartInfo(exe, "--startup") { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            EngineLog.Write($"[Watchdog] relaunch failed: {ex.Message}");
            return null;
        }
    }
}

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

        // Keep-alive mode: the same exe watches the real app (see Watchdog).
        if (args.Length >= 2 && string.Equals(args[0], "--watchdog", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(args[1], out var watchedPid))
        {
            Watchdog.Run(watchedPid);
            return;
        }

        // A crash must leave a trace: the stack goes to the engine log before the
        // runtime tears the process down (non-zero exit → the watchdog relaunches).
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            EngineLog.Write($"[Host] UNHANDLED EXCEPTION: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
            EngineLog.Write($"[Host] unobserved task exception: {e.Exception}");

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
        // Windows 11 ignores that request for a process without a foreground window
        // (and throttles it) unless it opts out — a tray audio app is exactly that
        // process. This is the "glitches while I work on the laptop" fix.
        EngineLog.Write(PowerManagement.DisablePowerThrottling()
            ? "[Host] power throttling disabled (execution speed + timer resolution)"
            : "[Host] power throttling opt-out not available on this OS");

        var bootConfig = AppConfig.Load();
        if (!args.Any(a => string.Equals(a, "--no-watchdog", StringComparison.OrdinalIgnoreCase))
            && (bootConfig?.KeepAlive ?? true))
        {
            Watchdog.Spawn();
        }

        // Rendering backend. GPU rendering of the window drives the NVIDIA display
        // driver, and that driver's DPC activity jitters every audio timer on the box
        // (HDMI audio sits on the GPU; the virtual cable's engine timer and the 3 ms
        // exclusive streams all degraded the moment the window was visible). Software
        // rendering costs nothing visible for this UI and keeps the GPU idle.
        s_gpuRendering = bootConfig?.GpuRendering ?? false;
        EngineLog.Write(s_gpuRendering ? "[Host] GPU rendering (config gpuRendering=true)" : "[Host] software rendering");

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

    private static bool s_gpuRendering;

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .With(new Win32PlatformOptions
        {
            RenderingMode = s_gpuRendering
                ? new[] { Win32RenderingMode.AngleEgl, Win32RenderingMode.Software }
                : new[] { Win32RenderingMode.Software },
        })
        .WithInterFont()
        .LogToTrace();
}

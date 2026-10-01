using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace AudioMatrixRouter;

/// <summary>Public face of the engine diagnostic log for the host process.</summary>
public static class EngineLog
{
    public static string Path => Audio.Wasapi.WasapiDiagnostics.Path_;
    public static void Write(string line) => Audio.Wasapi.WasapiDiagnostics.Log(line);
}

/// <summary>
/// Process heartbeat shared with the keep-alive watchdog through a small memory-mapped
/// file. The engine health tick writes a UTC timestamp every second while the UI thread
/// has itself reported alive recently; the watchdog relaunches the process when the
/// stamp goes stale (hung) or the process exits with an error (crashed).
/// </summary>
public static class ProcessHeartbeat
{
    public const string MapName = "Local\\AudioMatrixRouter.Heartbeat";
    public const int StaleAfterMs = 10_000;
    public const int UiStallMs = 15_000;

    private static MemoryMappedFile? s_map;
    private static MemoryMappedViewAccessor? s_view;
    private static long s_lastUiTick = Environment.TickCount64;
    private static readonly object s_lock = new();

    /// <summary>Called from the UI thread's 1 s timer.</summary>
    public static void NoteUiAlive() => Volatile.Write(ref s_lastUiTick, Environment.TickCount64);

    public static long UiAgeMs => Environment.TickCount64 - Volatile.Read(ref s_lastUiTick);

    /// <summary>Writes the stamp unless the UI thread has been silent for too long —
    /// in which case the process is hung from the user's point of view and the
    /// watchdog should recycle it.</summary>
    public static void Beat()
    {
        if (UiAgeMs > UiStallMs) return;
        lock (s_lock)
        {
            try
            {
                if (s_view is null)
                {
                    s_map = MemoryMappedFile.CreateOrOpen(MapName, 16);
                    s_view = s_map.CreateViewAccessor(0, 16);
                }
                s_view.Write(0, DateTime.UtcNow.Ticks);
                s_view.Flush();
            }
            catch
            {
                // Heartbeat is best-effort; a missing map only costs a watchdog restart.
            }
        }
    }

    /// <summary>Watchdog side: milliseconds since the last stamp, or -1 if there is none yet.</summary>
    public static long ReadAgeMs()
    {
        try
        {
            using var map = MemoryMappedFile.OpenExisting(MapName);
            using var view = map.CreateViewAccessor(0, 16);
            long ticks = view.ReadInt64(0);
            if (ticks <= 0) return -1;
            return (long)(DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc)).TotalMilliseconds;
        }
        catch
        {
            return -1;
        }
    }
}

/// <summary>
/// Windows power/scheduling knobs that keep a tray process honest under load.
/// </summary>
public static class PowerManagement
{
    private const uint ES_CONTINUOUS = 0x80000000;
    private const uint ES_SYSTEM_REQUIRED = 0x00000001;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint esFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    private const int ProcessPowerThrottling = 4;
    private const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
    private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;
    private const uint PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION = 0x4;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr process, int infoClass,
        ref PROCESS_POWER_THROTTLING_STATE info, int size);

    /// <summary>
    /// Opt out of Windows 11 power throttling. A process without a foreground window
    /// is otherwise put under EcoQoS and, worse, its timeBeginPeriod(1) is IGNORED,
    /// so timer-paced capture and every wait slips to 15.6 ms resolution under load.
    /// That is the "glitches while I work on the laptop" case. Returns false when the
    /// OS refuses (pre-Win10 1709); harmless then.
    /// </summary>
    public static bool DisablePowerThrottling()
    {
        try
        {
            var state = new PROCESS_POWER_THROTTLING_STATE
            {
                Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED | PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION,
                StateMask = 0, // controlled bits cleared = throttling OFF
            };
            return SetProcessInformation(System.Diagnostics.Process.GetCurrentProcess().Handle,
                ProcessPowerThrottling, ref state, Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Per-thread: call from one long-lived thread (the UI thread) each time.</summary>
    public static void SetKeepSystemAwake(bool keepAwake)
    {
        try
        {
            SetThreadExecutionState(keepAwake ? ES_CONTINUOUS | ES_SYSTEM_REQUIRED : ES_CONTINUOUS);
        }
        catch
        {
        }
    }
}

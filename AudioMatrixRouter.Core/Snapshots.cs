namespace AudioMatrixRouter;

/// <summary>
/// UI-facing data contracts shared by every host (WinForms/WebView today, Avalonia
/// tomorrow). These are the in-process successors of the old bridge JSON DTOs.
/// </summary>
public sealed record DeviceSnapshot(
    string Id,
    string Name,
    int Channels,
    int Offset,
    bool IsMaster,
    int DelayMs,
    int SampleRate,
    bool IsLoopback,
    bool IsActive);

public sealed record RouteSnapshot(int InCh, int OutCh, float GainDb, bool PhaseInverted);

public sealed class UiSnapshot
{
    public long Rev { get; set; }
    public bool Running { get; set; }
    public bool Locked { get; set; }
    public bool StartupAtBoot { get; set; }
    public int InputBufferMs { get; set; }
    public int OutputBufferMs { get; set; }
    public string InputDeviceMode { get; set; } = "both";
    public List<DeviceSnapshot> Inputs { get; set; } = [];
    public List<DeviceSnapshot> Outputs { get; set; } = [];
    public List<DeviceSnapshot> AvailableInputs { get; set; } = [];
    public List<DeviceSnapshot> AvailableOutputs { get; set; } = [];
    public List<RouteSnapshot> Routes { get; set; } = [];
}

public sealed class DeviceMetrics
{
    public string DeviceId { get; set; } = "";
    public float[] PeakLevels { get; set; } = [];
    public long Overflows { get; set; }
    public long DroppedFrames { get; set; }
    public long Underruns { get; set; }
    public long SyncCorrections { get; set; }
    /// <summary>Real WASAPI engine period this endpoint runs at, ms (0 = n/a).</summary>
    public double PeriodMs { get; set; }
    /// <summary>Achieved period tier ("MinPeriod", "DefaultPeriod", "Legacy"...).</summary>
    public string TierName { get; set; } = "";
    public long PostRecoveryUnderruns { get; set; }
    public double? LatencyMs { get; set; }
    public double? SyncErrorMs { get; set; }
    public double? AppliedPpm { get; set; }
    public double? VariationRangeMs { get; set; }
    public bool FastCatchUpActive { get; set; }
    public double FastCatchUpDutyPercent { get; set; }
}

public sealed record RouteLatency(int InCh, int OutCh, double WorkingLatencyMs);

public sealed class MetricsSnapshot
{
    public bool Running { get; set; }
    public double? TotalLatencyMs { get; set; }
    public double? InputLatencyMs { get; set; }
    public double? OutputLatencyMs { get; set; }
    public double? InputJitterMs { get; set; }
    public List<DeviceMetrics> Inputs { get; set; } = [];
    public List<DeviceMetrics> Outputs { get; set; } = [];
    public List<RouteLatency> RouteLatencies { get; set; } = [];
}

/// <summary>One requested crosspoint change, device-relative (the only sane addressing).</summary>
public sealed record RouteRequest(
    string InDeviceId,
    int InChannel,
    string OutDeviceId,
    int OutChannel,
    bool Active,
    float GainDb,
    bool PhaseInverted);

/// <summary><paramref name="DownloadBytes"/> is what will actually be fetched —
/// the delta packages when Velopack can patch, otherwise the full package.</summary>
public sealed record UpdateCheckResult(
    string CurrentVersion,
    string? AvailableVersion,
    bool Portable,
    long DownloadBytes = 0);

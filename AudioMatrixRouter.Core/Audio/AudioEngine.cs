using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Threading;

namespace AudioMatrixRouter.Audio;

public class ActiveDevice
{
    /// <summary>Enumerated identity and format. Replaced (never mutated) when the
    /// endpoint's live mix format turns out to differ — see the format guards.</summary>
    public required DeviceInfo Info { get; set; }
    public int GlobalChannelOffset { get; set; }
    public RingBuffer? RingBuffer { get; set; }
    public ICaptureEndpoint? Capture { get; set; }
    public IRenderEndpoint? Render { get; set; }
    public MixingSampleProvider? MixProvider { get; set; }
    public InputAsrc? InputAsrc { get; set; }
    public bool IsMasterDevice { get; set; }
    public int OutputDelayMs { get; set; }
    public string ConsumerId { get; set; } = string.Empty;
    public long InputOverflowCount;
    public int CaptureLatencyMs { get; set; }
    public int RenderLatencyMs { get; set; }
    /// <summary>Real capture delivery cadence, ms (from the endpoint; loopback 10).</summary>
    public double CaptureCadenceMs { get; set; }
    /// <summary>Cadence the budget uses for this input: max(nominal period, worst MEASURED
    /// recurring gap). Raised live by the health tick; never lowered within a session.</summary>
    public double EffectiveCadenceMs { get; set; }
    /// <summary>This input's own ring fill target (engine frames), from its effective cadence.</summary>
    public int FillTargetFrames { get; set; }
    /// <summary>Why this endpoint did not open exclusive (empty when it did, or was not asked).</summary>
    public string ExclusiveRefusal { get; set; } = string.Empty;
    /// <summary>Set when this capture could not sustain its exclusive period (session).</summary>
    public bool ExclusiveCaptureDemoted { get; set; }
    /// <summary>True until the first measured cadence replaces the start-up prior.</summary>
    public bool CadenceIsPrior { get; set; }
    // Read back from WASAPI after Initialize (0 = read-back unavailable): the REAL
    // allocated render buffer and engine period, not the requested values.
    public int RealRenderBufferMs { get; set; }
    public int RenderPeriodMs { get; set; }
    public bool IsLoopback { get; set; }
    /// <summary>The period tier the next (re)start should request for this device
    /// (adaptive degrade demotes it; survives Stop/Start).</summary>
    public EndpointTier RequestedRenderTier { get; set; } = EndpointTier.MinPeriod;
    public EndpointTier RequestedCaptureTier { get; set; } = EndpointTier.MinPeriod;
    // Adaptive-degrade bookkeeping (per session, survives Stop/Start).
    public long TierUnderrunBaseline { get; set; }
    public long TierDiscontinuityBaseline { get; set; }
    public long TierStartTimestamp { get; set; }
    public int TierDegradeCount { get; set; }
    public double BaseLatencyMs { get; set; }
    /// <summary>Set once the health monitor has faulted this endpoint (no repeats per session).</summary>
    public bool HealthFaulted { get; set; }
    // Per-channel running peak (0..1). Producer writes; UI samples and resets atomically.
    public float[]? PeakLevels;
}

/// <summary>
/// AudioEngine — sync-architecture rewrite.
///
/// Clocking model:
///   * ENGINE CLOCK = master output device's nominal sample rate / render callback.
///   * Capture side: each input's WASAPI callback feeds an InputAsrc that converts the
///     capture stream (any rate, any crystal) into engine-rate frames before they enter
///     the shared ring. A fill-level PI per input absorbs capture-crystal drift, so the
///     master/input relationship can no longer drift unboundedly.
///   * Render side: each output's MixingSampleProvider consumes engine-rate frames and
///     resamples to its device rate; followers carry a ppm trim from the coordinator's
///     cursor-truth phase loop. The master output plays at exactly 1.0.
///
/// Startup ordering (matters for phase zero):
///   rings → coordinator → providers (consumer cursors pinned on empty rings)
///   → renders initialised → captures started → renders played together.
///   All consumers therefore observe identical stream positions; the prefill barrier
///   releases everyone simultaneously with timelines zeroed together.
/// </summary>
public class AudioEngine : IDisposable
{
    private const int DefaultInputRingBufferMs = 80;
    private const int DefaultOutputBufferMs = 100;
    // Ring capacity stays at a stable ceiling; sliders move targets, not allocations.
    private const int RingBufferCapacityMs = 400;
    private const int RenderPeriodMs = 10;
    // ===== Latency budget =====
    // The user's knob (_outputBufferMs, kept under its historic name for config
    // compatibility) is the TARGET END-TO-END LATENCY. The engine splits it:
    //   capture buffer  : 10 ms fixed (the WASAPI shared-mode period — nothing gained
    //                     by making it configurable, plenty lost when it was out/2)
    //   render buffer   : a quarter of the budget, 10..50 ms
    //   ring fill target: whatever remains — floored at (render + 10 ms) so one render
    //                     gulp plus one capture block can never starve the ring
    // Reported total ≈ capture + fill + render ≈ the knob value, so what the user sets
    // is what the tiles show. Below ~40 ms the stability floors win over the split.

    private readonly DeviceEnumerator _enumerator = new();
    private readonly List<ActiveDevice> _inputDevices = [];
    private readonly List<ActiveDevice> _outputDevices = [];
    private readonly RoutingMatrix _routingMatrix = new();
    private bool _running;
    private OutputSyncCoordinator? _syncCoordinator;
    private int _inputBufferMs = DefaultInputRingBufferMs;
    private int _outputBufferMs = DefaultOutputBufferMs;
    private int _engineSampleRate = 48000;
    // Durable user choice of clock master. Kept even while the device is absent so it can
    // be promoted back the moment it reappears (the IsMasterDevice flags only reflect the
    // currently running session).
    private string? _preferredOutputMasterId;
    // Same durable-preference semantics for the input master (UI/telemetry only — the
    // sync architecture disciplines every input against the engine clock regardless).
    private string? _preferredInputMasterId;
    // Device-batch state: while depth > 0, Add/Remove skip their per-call Stop/Start so a
    // multi-device operation costs at most one engine restart (performed on batch dispose).
    private int _deviceBatchDepth;
    private bool _batchRestart;

    /// <summary>
    /// Stores routes that were active but whose devices became unavailable.
    /// These routes are preserved so they can be restored when devices reconnect.
    /// </summary>
    private readonly List<RoutedCrosspoint> _dormantRoutes = [];

    public readonly record struct RoutedCrosspoint(
        string InputDeviceId,
        int InputLocalChannel,
        string OutputDeviceId,
        int OutputLocalChannel,
        bool Active,
        float GainDb,
        bool PhaseInverted = false);

    public event Action? DevicesChanged;
    public event Action? StateChanged;

    public IReadOnlyList<ActiveDevice> InputDevices => _inputDevices;
    public IReadOnlyList<ActiveDevice> OutputDevices => _outputDevices;
    public RoutingMatrix RoutingMatrix => _routingMatrix;
    public IReadOnlyList<RoutedCrosspoint> DormantRoutes => _dormantRoutes;
    public bool IsRunning => _running;
    public DeviceEnumerator Enumerator => _enumerator;

    public int TotalInputChannels { get; private set; }
    public int TotalOutputChannels { get; private set; }
    public int InputBufferMs => _inputBufferMs;
    public int OutputBufferMs => _outputBufferMs;

    /// <summary>"auto" (IAudioClient3 clients) or "legacy" (NAudio). Applied at the
    /// next engine start.</summary>
    public string Backend => _backend;
    private string _backend = "auto";

    public void SetBackend(string? backend)
    {
        _backend = string.Equals(backend, "legacy", StringComparison.OrdinalIgnoreCase)
            ? "legacy"
            : "auto";
    }

    // ===== exclusive mode (opt-in, off by default) =====
    // Outputs are all-or-nothing per start: the sync coordinator assumes equal FIFO
    // depth on every output, and a 3 ms exclusive FIFO beside a 12 ms shared one would
    // play 9 ms early. Captures are independent (each ring has its own target).
    private bool _exclusiveMode;
    private bool _exclusiveDemoted;        // an output could not sustain its exclusive period this session
    private bool _exclusiveRenderActive;   // every started output runs exclusive (budget branch)
    private string? _pendingExclusiveBlock; // reason carried into the shared re-start

    public bool ExclusiveMode => _exclusiveMode;
    /// <summary>True while the running session has every output on an exclusive stream.</summary>
    public bool ExclusiveActive => _running && _exclusiveRenderActive;
    /// <summary>Why exclusive mode is requested but not in effect (empty otherwise).</summary>
    public string ExclusiveBlockedReason { get; private set; } = string.Empty;

    /// <summary>Opt in/out of exclusive-mode streams. Applied at the next start; a running
    /// engine restarts. Resets the AUTO loop's memory because the floor changes.</summary>
    public bool SetExclusiveMode(bool enabled)
    {
        if (_exclusiveMode == enabled) return true;
        _exclusiveMode = enabled;
        _exclusiveDemoted = false;
        foreach (var d in _inputDevices) d.ExclusiveCaptureDemoted = false;
        _autoHeadroomMs = 0;
        _learnedMinHeadroomMs = 0;
        _safetyPeriods = 0;
        Wasapi.WasapiDiagnostics.Log($"[Engine] exclusive mode {(enabled ? "ON" : "off")}");
        if (_running) FullRestart();
        StateChanged?.Invoke();
        return true;
    }

    /// <summary>An endpoint died mid-stream (device invalidated / stalled). Raised
    /// from an audio thread — the host must marshal and drive recovery from there
    /// (AppController debounces into ReloadEngine). Never restarts inline.</summary>
    public event Action? EngineFaulted;

    private void OnEndpointFaulted(string deviceName, int hr)
    {
        Wasapi.WasapiDiagnostics.Log($"[Engine] endpoint FAULTED: {deviceName} hr=0x{hr:X8}");
        try { EngineFaulted?.Invoke(); } catch { }
    }

    // ===================================================================== adaptive degrade
    // Watches the first ~10s of every small-period tier. A driver that advertises a
    // small period but can't sustain it shows up as an underrun (render) or
    // discontinuity/overflow (capture) burst — demote that device one rung and
    // restart through the NORMAL start path (barrier/fill/phase logic reused).
    // Tier state lives on ActiveDevice and survives Stop/Start; two demotions pin
    // the device at the default period for the session. The timer self-disarms
    // when nothing is left to evaluate.

    private Timer? _healthTimer;
    private int _healthBusy;

    // ===================================================================== health tick
    // One 1 s timer for everything that watches the engine from outside the audio
    // threads: adaptive tier degrade, the AUTO headroom loop, endpoint health, and the
    // process heartbeat the keep-alive watchdog reads. Runs for the life of the
    // engine (armed in Init); each step checks _running itself.

    private void ArmHealthTimer()
    {
        _healthTimer?.Dispose();
        _healthTimer = new Timer(_ => HealthTick(), null, 1000, 1000);
    }

    private void HealthTick()
    {
        if (Interlocked.CompareExchange(ref _healthBusy, 1, 0) != 0) return;
        try
        {
            ProcessHeartbeat.Beat();
            if (!_running) return;
            EvaluateMeasuredCadence();
            EvaluateAdaptiveDegrade();
            EvaluateAutoHeadroom();
            CheckEndpointHealth();
        }
        catch (Exception ex)
        {
            Wasapi.WasapiDiagnostics.Log($"[Engine] health tick error: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _healthBusy, 0);
        }
    }

    // ----- AUTO headroom loop -----
    // Underruns must never just climb. Per tick: underruns over the last 10 s across
    // started outputs (already counted only against LIVE producers). Three or more →
    // one capture cadence of extra ring headroom (cap 40 ms, 10 s cooldown). Still
    // underrunning at the cap for 30 s → safety tier (one more render period of
    // padding). Five clean minutes → step back down, one notch at a time. The drum
    // caption shows the effective value, so the user sees it move.
    private const int AutoWindowSec = 10;
    private const long AutoRaiseThreshold = 3;
    private const int AutoCooldownMs = 10_000;
    private const int AutoSafetyAfterMs = 30_000;
    private const int AutoDecayAfterMs = 300_000;
    private readonly long[] _underrunWindow = new long[AutoWindowSec];
    private int _underrunWindowIdx;
    private long _lastUnderrunTotal;
    private long _lastAutoChangeAt;
    private long _atMaxSince;
    private long _lastUnderrunSeenAt;
    // Decay memory: the headroom level at which the last storm happened is unsafe for
    // this session; decay stops one step above it instead of walking back into the
    // storm every five minutes (the 54 → 84 → 54 cycle seen in the field).
    private int _learnedMinHeadroomMs;
    // Start-up transient (barrier release, first re-anchors) books a handful of
    // underruns that are not a sizing problem; the loop ignores the first seconds.
    private const int AutoStartGraceMs = 3000;
    private long _runningSince;

    private void ResetAutoWindow()
    {
        Array.Clear(_underrunWindow);
        _underrunWindowIdx = 0;
        _lastUnderrunTotal = 0;
    }

    private void EvaluateAutoHeadroom()
    {
        long total = 0;
        foreach (var dev in _outputDevices.ToArray())
        {
            if (dev.MixProvider is { } p) total += p.UnderrunCount;
        }
        long delta = total >= _lastUnderrunTotal ? total - _lastUnderrunTotal : 0;
        _lastUnderrunTotal = total;
        _underrunWindow[_underrunWindowIdx] = delta;
        _underrunWindowIdx = (_underrunWindowIdx + 1) % _underrunWindow.Length;
        long windowSum = 0;
        foreach (var v in _underrunWindow) windowSum += v;

        long now = Environment.TickCount64;
        if (windowSum > 0) _lastUnderrunSeenAt = now;
        int step = (int)Math.Ceiling(_worstCaptureCadenceMs > 0 ? _worstCaptureCadenceMs : 10);

        if (now - _runningSince < AutoStartGraceMs)
        {
            ResetAutoWindow();
            _lastUnderrunTotal = total;
            return;
        }

        if (windowSum >= AutoRaiseThreshold && now - _lastAutoChangeAt >= AutoCooldownMs)
        {
            if (_autoHeadroomMs < LatencyBudget.MaxAutoHeadroomMs)
            {
                _learnedMinHeadroomMs = Math.Max(_learnedMinHeadroomMs,
                    Math.Min(LatencyBudget.MaxAutoHeadroomMs, _autoHeadroomMs + step));
                _autoHeadroomMs = Math.Min(LatencyBudget.MaxAutoHeadroomMs, _autoHeadroomMs + step);
                _lastAutoChangeAt = now;
                _atMaxSince = _autoHeadroomMs >= LatencyBudget.MaxAutoHeadroomMs ? now : 0;
                ResetAutoWindow();
                _lastUnderrunTotal = total;
                ApplyBudgetLive($"auto headroom +{step} ms after {windowSum} underruns in {AutoWindowSec} s");
            }
            else if (_safetyPeriods == 0 && _atMaxSince != 0 && now - _atMaxSince >= AutoSafetyAfterMs)
            {
                _safetyPeriods = 1;
                _lastAutoChangeAt = now;
                ResetAutoWindow();
                _lastUnderrunTotal = total;
                ApplyBudgetLive($"safety tier ON: {windowSum} underruns in {AutoWindowSec} s at max headroom");
            }
            return;
        }

        if ((_autoHeadroomMs > 0 || _safetyPeriods > 0)
            && now - _lastUnderrunSeenAt >= AutoDecayAfterMs
            && now - _lastAutoChangeAt >= AutoDecayAfterMs)
        {
            if (_safetyPeriods > 0)
            {
                _safetyPeriods = 0;
            }
            else
            {
                int next = Math.Max(_learnedMinHeadroomMs, _autoHeadroomMs - step);
                if (next == _autoHeadroomMs)
                {
                    // Sitting on the learned floor: nothing to give back this session.
                    _lastAutoChangeAt = now;
                    return;
                }
                _autoHeadroomMs = next;
            }
            _lastAutoChangeAt = now;
            _atMaxSince = 0;
            ApplyBudgetLive("auto headroom decay after 5 clean minutes");
        }
    }

    // ----- measured cadence -----
    // The budget's capture term must be what the producer DOES, not its stream period:
    // a virtual cable delivers in ~16 ms bursts behind a 10 ms period, and a 22 ms ring
    // floor sized for 10 ms starves on every burst (the underrun storms that drove the
    // AUTO loop to +40 ms). Each client keeps a gap histogram; a gap that recurs is
    // adopted as that input's cadence, live, and never lowered within the session.
    private void EvaluateMeasuredCadence()
    {
        bool changed = false;
        double worst = 0;
        foreach (var dev in _inputDevices.ToArray())
        {
            var cap = dev.Capture;
            if (cap is null) continue;
            double nominal = cap.ActualPeriodMs > 0 ? cap.ActualPeriodMs : 10;
            double measured = cap.MeasuredCadenceMs;
            double effective = Math.Max(nominal, Math.Ceiling(measured));
            // The start-up prior (loopbacks: twice the period, because virtual cables
            // burst) is replaced by the first real measurement in either direction;
            // after that the cadence only ever rises within the session.
            bool replacePrior = dev.CadenceIsPrior && measured > 0;
            if (replacePrior) dev.CadenceIsPrior = false;
            if (effective > dev.EffectiveCadenceMs + 0.5 || (replacePrior && Math.Abs(effective - dev.EffectiveCadenceMs) > 0.5))
            {
                Wasapi.WasapiDiagnostics.Log($"[Engine] measured cadence '{dev.Info.Name}': " +
                    $"{dev.EffectiveCadenceMs:0.##} → {effective:0.##} ms (stream period {nominal:0.##} ms)");
                dev.EffectiveCadenceMs = effective;
                dev.CaptureCadenceMs = effective;
                dev.CaptureLatencyMs = (int)Math.Ceiling(effective);
                dev.InputAsrc?.SetCadenceMs(effective);
                if (dev.RingBuffer is { } ring) ring.ProducerCadenceMs = effective;
                changed = true;
            }
            if (dev.EffectiveCadenceMs > worst) worst = dev.EffectiveCadenceMs;
        }
        if (!changed) return;

        // Worst over the inputs' effective cadences. It can drop once, when a loopback
        // prior is replaced by its measurement; after that it only rises.
        double delta = worst - _worstCaptureCadenceMs;
        _worstCaptureCadenceMs = worst;
        // Headroom the AUTO loop added for this very burstiness now lives in the floor:
        // hand it back so the honest floor does not stack on top of the guessed one.
        if (delta > 0 && _autoHeadroomMs > 0)
        {
            int give = (int)Math.Min(_autoHeadroomMs, Math.Ceiling(delta));
            _autoHeadroomMs -= give;
            _learnedMinHeadroomMs = Math.Max(0, _learnedMinHeadroomMs - give);
        }
        ApplyBudgetLive($"measured cadence {worst:0.##} ms");
    }

    // ----- endpoint health -----
    // A render thread that stops reporting blocks while the engine runs (device
    // asleep, thread dead) is faulted through the normal backoff path. The client's
    // own stall detector usually fires first; this is the belt under it.
    private const double RenderSilentFaultMs = 2000;
    private const double RenderNeverReportedFaultMs = 5000;

    private void CheckEndpointHealth()
    {
        var coordinator = _syncCoordinator;
        if (coordinator is null) return;
        double freq = System.Diagnostics.Stopwatch.Frequency;
        foreach (var dev in _outputDevices.ToArray())
        {
            if (dev.Render is null || dev.MixProvider is null || dev.HealthFaulted) continue;
            double age = coordinator.GetConsumerReportAgeMs(dev.ConsumerId);
            double sinceStartMs = (System.Diagnostics.Stopwatch.GetTimestamp() - dev.TierStartTimestamp) * 1000.0 / freq;
            bool silent = age >= 0 ? age > RenderSilentFaultMs : sinceStartMs > RenderNeverReportedFaultMs;
            if (!silent) continue;
            dev.HealthFaulted = true;
            Wasapi.WasapiDiagnostics.Log($"[Engine] health: '{dev.Info.Name}' render silent for " +
                $"{(age >= 0 ? age : sinceStartMs):0} ms — faulting");
            OnEndpointFaulted(dev.Info.Name, Wasapi.WasapiConstants.AUDCLNT_E_STALLED);
        }
    }

    private void EvaluateAdaptiveDegrade()
    {
        if (!_running) return;
        {
            const double WindowSec = 10;
            const long Threshold = 5;
            bool anyEvaluating = false;
            bool demoted = false;
            double freq = System.Diagnostics.Stopwatch.Frequency;

            foreach (var dev in _outputDevices)
            {
                if (dev.Render is null) continue;
                if (dev.Render.Tier is not (EndpointTier.Exclusive or EndpointTier.MinPeriod or EndpointTier.DoublePeriod)) continue;
                double elapsed = (System.Diagnostics.Stopwatch.GetTimestamp() - dev.TierStartTimestamp) / freq;
                if (elapsed > WindowSec) continue;
                anyEvaluating = true;

                long delta = (dev.MixProvider?.UnderrunCount ?? 0) - dev.TierUnderrunBaseline;
                // Exclusive callbacks come 3-5x more often, and a ring-sizing problem
                // (the AUTO loop's job) must not read as "the device cannot sustain its
                // period": demand a sustained burst before giving exclusive up.
                const long ExclusiveThreshold = 150;
                if (delta > ExclusiveThreshold && dev.Render.Tier == EndpointTier.Exclusive && !_exclusiveDemoted
                    && _autoHeadroomMs >= LatencyBudget.MaxAutoHeadroomMs)
                {
                    // All-or-nothing: one output off exclusive means every output goes shared.
                    _exclusiveDemoted = true;
                    Wasapi.WasapiDiagnostics.Log($"[Engine] adaptive degrade: '{dev.Info.Name}' could not sustain its exclusive period — exclusive off for this session");
                    demoted = true;
                }
                else if (delta > Threshold && dev.Render.Tier != EndpointTier.Exclusive && dev.TierDegradeCount < 2)
                {
                    dev.RequestedRenderTier = dev.RequestedRenderTier == EndpointTier.MinPeriod
                        ? EndpointTier.DoublePeriod
                        : EndpointTier.DefaultPeriod;
                    dev.TierDegradeCount++;
                    demoted = true;
                }
            }

            foreach (var dev in _inputDevices)
            {
                var cap = dev.Capture;
                if (cap is null) continue;
                if (cap.Tier is not (EndpointTier.Exclusive or EndpointTier.MinPeriod or EndpointTier.DoublePeriod)) continue;
                anyEvaluating = true;

                long delta = cap.DiscontinuityCount + Interlocked.Read(ref dev.InputOverflowCount)
                             - dev.TierDiscontinuityBaseline;
                if (delta > 50 && cap.Tier == EndpointTier.Exclusive && !dev.ExclusiveCaptureDemoted)
                {
                    dev.ExclusiveCaptureDemoted = true;
                    Wasapi.WasapiDiagnostics.Log($"[Engine] adaptive degrade: capture '{dev.Info.Name}' could not sustain its exclusive period — shared for this session");
                    demoted = true;
                }
                else if (delta > Threshold && cap.Tier != EndpointTier.Exclusive && dev.TierDegradeCount < 2)
                {
                    dev.RequestedCaptureTier = dev.RequestedCaptureTier == EndpointTier.MinPeriod
                        ? EndpointTier.DoublePeriod
                        : EndpointTier.DefaultPeriod;
                    dev.TierDegradeCount++;
                    demoted = true;
                }
            }

            if (demoted)
            {
                // Timer thread: never mutate engine state here (the host thread may be
                // inside SetCrosspoints/RefreshDevices). Ask the host for a restart
                // through the same marshalled, debounced path a fault uses.
                Wasapi.WasapiDiagnostics.Log("[Engine] adaptive degrade: tier demoted, requesting restart");
                try { EngineFaulted?.Invoke(); } catch { }
            }
            _ = anyEvaluating;
        }
    }

    public bool TryGetRouteWorkingLatencyMs(int inCh, int outCh, out double latencyMs)
    {
        latencyMs = 0;
        if (inCh < 0 || outCh < 0) return false;

        var matrix = _routingMatrix;
        if (inCh >= matrix.InputChannels || outCh >= matrix.OutputChannels) return false;

        var input = _inputDevices.FirstOrDefault(d => inCh >= d.GlobalChannelOffset && inCh < d.GlobalChannelOffset + d.Info.Channels);
        var output = _outputDevices.FirstOrDefault(d => outCh >= d.GlobalChannelOffset && outCh < d.GlobalChannelOffset + d.Info.Channels);
        if (input == null || output == null || input.RingBuffer == null) return false;

        var consumerId = string.IsNullOrWhiteSpace(output.ConsumerId) ? output.Info.Id : output.ConsumerId;
        int queuedFrames = input.RingBuffer.GetAvailableFrames(consumerId);
        // Rings hold engine-rate frames after the input ASRC stage.
        double captureQueueMs = _engineSampleRate > 0
            ? (queuedFrames * 1000.0) / _engineSampleRate
            : 0;

        int captureDriverMs = input.CaptureLatencyMs > 0 ? input.CaptureLatencyMs : _inputBufferMs;
        int renderDriverMs = output.RenderLatencyMs > 0 ? output.RenderLatencyMs : RenderPeriodMs;

        latencyMs = captureDriverMs + captureQueueMs + renderDriverMs + output.OutputDelayMs;
        return true;
    }

    /// <summary>
    /// Working latency for every active route in one pass. Ring fill is queried once per
    /// input/output device pair (not once per channel pair), which matters because each
    /// query takes the ring's audio-path lock — the old per-route path acquired it
    /// hundreds of times per second on large matrices from the UI thread.
    /// </summary>
    public List<(int InCh, int OutCh, double LatencyMs)> GetActiveRouteLatencies()
    {
        var result = new List<(int InCh, int OutCh, double LatencyMs)>();
        var matrix = _routingMatrix;
        var front = matrix.GetFrontBuffer();
        int outChannels = matrix.OutputChannels;
        if (front.Length == 0 || outChannels == 0) return result;

        var queueMsCache = new Dictionary<(string InputId, string ConsumerId), double>();
        for (int inCh = 0; inCh < matrix.InputChannels; inCh++)
        {
            var input = FindInputDeviceByChannel(inCh);
            if (input == null || input.RingBuffer == null) continue;

            for (int outCh = 0; outCh < outChannels; outCh++)
            {
                int idx = inCh * outChannels + outCh;
                if (idx >= front.Length || !front[idx].Active) continue;

                var output = FindOutputDeviceByChannel(outCh);
                if (output == null) continue;

                var consumerId = string.IsNullOrWhiteSpace(output.ConsumerId) ? output.Info.Id : output.ConsumerId;
                var cacheKey = (input.Info.Id, consumerId);
                if (!queueMsCache.TryGetValue(cacheKey, out var queueMs))
                {
                    int queuedFrames = input.RingBuffer.GetAvailableFrames(consumerId);
                    queueMs = _engineSampleRate > 0 ? (queuedFrames * 1000.0) / _engineSampleRate : 0;
                    queueMsCache[cacheKey] = queueMs;
                }

                int captureDriverMs = input.CaptureLatencyMs > 0 ? input.CaptureLatencyMs : _inputBufferMs;
                int renderDriverMs = output.RenderLatencyMs > 0 ? output.RenderLatencyMs : RenderPeriodMs;
                result.Add((inCh, outCh, captureDriverMs + queueMs + renderDriverMs + output.OutputDelayMs));
            }
        }

        return result;
    }

    /// <summary>
    /// Worst capture+queue latency across ACTIVE routes — the input-side figure that
    /// pairs with the route-based total. The master-input variant below measures a
    /// device nobody may be routed to (an idle loopback master reads high and means
    /// nothing), which made total−input go negative on the metrics tiles.
    /// </summary>
    public bool TryGetRoutedInputPathLatencyMs(out double latencyMs)
    {
        latencyMs = 0;
        bool any = false;
        var matrix = _routingMatrix;
        var front = matrix.GetFrontBuffer();
        int outChannels = matrix.OutputChannels;
        if (front.Length == 0 || outChannels == 0 || _engineSampleRate <= 0) return false;

        var seen = new HashSet<(string InputId, string ConsumerId)>();
        for (int inCh = 0; inCh < matrix.InputChannels; inCh++)
        {
            var input = FindInputDeviceByChannel(inCh);
            if (input?.RingBuffer == null) continue;

            for (int outCh = 0; outCh < outChannels; outCh++)
            {
                int idx = inCh * outChannels + outCh;
                if (idx >= front.Length || !front[idx].Active) continue;

                var output = FindOutputDeviceByChannel(outCh);
                if (output == null) continue;

                var consumerId = string.IsNullOrWhiteSpace(output.ConsumerId) ? output.Info.Id : output.ConsumerId;
                if (!seen.Add((input.Info.Id, consumerId))) continue;

                int captureDriverMs = input.CaptureLatencyMs > 0 ? input.CaptureLatencyMs : _inputBufferMs;
                double queueMs = (input.RingBuffer.GetAvailableFrames(consumerId) * 1000.0) / _engineSampleRate;
                var path = captureDriverMs + queueMs;
                if (path > latencyMs) latencyMs = path;
                any = true;
            }
        }
        return any;
    }

    public bool TryGetInputPathLatencyMs(out double latencyMs)
    {
        latencyMs = 0;

        var inputMaster = GetInputMasterDevice();
        if (inputMaster == null || inputMaster.RingBuffer == null)
        {
            return false;
        }

        var outputMaster = GetOutputMasterDevice();
        var consumerId = outputMaster == null
            ? string.Empty
            : (string.IsNullOrWhiteSpace(outputMaster.ConsumerId) ? outputMaster.Info.Id : outputMaster.ConsumerId);
        if (string.IsNullOrWhiteSpace(consumerId))
        {
            consumerId = inputMaster.Info.Id;
        }

        int captureDriverMs = inputMaster.CaptureLatencyMs > 0 ? inputMaster.CaptureLatencyMs : _inputBufferMs;

        double queueMs = 0;
        if (_engineSampleRate > 0)
        {
            int queuedFrames = inputMaster.RingBuffer.GetAvailableFrames(consumerId);
            queueMs = (queuedFrames * 1000.0) / _engineSampleRate;
        }

        latencyMs = captureDriverMs + queueMs;
        return true;
    }

    public bool TryGetOutputPathLatencyMs(out double latencyMs)
    {
        latencyMs = 0;

        var outputMaster = GetOutputMasterDevice();
        if (outputMaster == null)
        {
            return false;
        }

        int renderDriverMs = outputMaster.RenderLatencyMs > 0 ? outputMaster.RenderLatencyMs : RenderPeriodMs;
        latencyMs = renderDriverMs + outputMaster.OutputDelayMs;
        return true;
    }

    /// <summary>
    /// Measured input-side timing jitter: the worst peak-to-peak ring-fill excursion (ms)
    /// across all inputs since the last call. A true engine measurement of capture/render
    /// callback timing interplay — replaces the old UI's poll-delta sampling noise.
    /// </summary>
    public bool TryGetInputJitterMs(out double jitterMs)
    {
        jitterMs = 0;
        bool any = false;
        foreach (var dev in _inputDevices)
        {
            var sample = dev.InputAsrc?.GetAndResetFillJitterMs();
            if (sample == null) continue;
            any = true;
            if (sample.Value > jitterMs) jitterMs = sample.Value;
        }
        return any;
    }

    public void Init()
    {
        _enumerator.SetChangeCallback(() => DevicesChanged?.Invoke());
        ArmHealthTimer();
    }

    public bool SetInputMasterDevice(string deviceId)
    {
        // The input-master flag is retained for UI/telemetry; the sync architecture
        // disciplines every input independently against the engine clock, so it has
        // no controller role anymore.
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            _preferredInputMasterId = null;
            bool cleared = false;
            foreach (var d in _inputDevices)
            {
                if (d.IsMasterDevice)
                {
                    d.IsMasterDevice = false;
                    cleared = true;
                }
            }

            if (cleared)
            {
                StateChanged?.Invoke();
            }

            return true;
        }

        // Remember the choice even when the device is currently absent, so it can be
        // promoted the moment it (re)appears — same semantics as the output master.
        _preferredInputMasterId = deviceId;

        var device = _inputDevices.FirstOrDefault(d => d.Info.Id == deviceId);
        if (device == null)
        {
            StateChanged?.Invoke();
            return false;
        }

        bool changed = false;
        foreach (var d in _inputDevices)
        {
            bool next = d.Info.Id == deviceId;
            if (d.IsMasterDevice != next)
            {
                d.IsMasterDevice = next;
                changed = true;
            }
        }

        if (changed)
        {
            StateChanged?.Invoke();
        }

        return true;
    }

    public bool SetOutputMasterDevice(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            _preferredOutputMasterId = null;
            bool cleared = false;
            foreach (var d in _outputDevices)
            {
                if (d.IsMasterDevice)
                {
                    d.IsMasterDevice = false;
                    cleared = true;
                }
            }

            // Changing the clock reference mid-flight requires a clean re-anchor.
            if (_running) FullRestart();

            if (cleared)
            {
                StateChanged?.Invoke();
            }

            return true;
        }

        // Remember the choice even when the device is currently absent, so it can be
        // promoted the moment it (re)appears instead of being silently forgotten.
        _preferredOutputMasterId = deviceId;

        var device = _outputDevices.FirstOrDefault(d => d.Info.Id == deviceId);
        if (device == null)
        {
            // Preference remembered; false tells the caller it did not take effect yet.
            StateChanged?.Invoke();
            return false;
        }

        bool changed = false;
        foreach (var d in _outputDevices)
        {
            bool next = d.Info.Id == deviceId;
            if (d.IsMasterDevice != next)
            {
                d.IsMasterDevice = next;
                changed = true;
            }
        }

        // A parked output (no active route) cannot hold the engine clock: it has no
        // render endpoint to derive one from. Record the choice, skip the restart —
        // restarting would only re-elect the same routed device, glitching every
        // playing output for nothing — and report that it is not the live clock yet.
        var routedIds = _running ? GetRoutedOutputIdsFromMatrix() : null;
        bool carriesRoute = routedIds is null || routedIds.Count == 0 || routedIds.Contains(deviceId);
        if (!carriesRoute)
        {
            if (changed) StateChanged?.Invoke();
            return false;
        }

        // The master output DEFINES the engine clock; switching it changes the clock
        // domain, so restart cleanly rather than re-pointing controllers mid-stream.
        if (changed && _running) FullRestart();

        if (changed)
        {
            StateChanged?.Invoke();
        }

        return true;
    }

    public string? PreferredOutputMasterId => _preferredOutputMasterId;
    public string? PreferredInputMasterId => _preferredInputMasterId;

    // Masters for PERSISTENCE: the user's choice as carried by the session flags, with
    // no runtime substitution. GetOutputMasterDevice() may answer with a live stand-in
    // when the chosen device is parked or failed — writing THAT to disk would quietly
    // promote a temporary fallback into the user's saved preference.
    public string? FlaggedOutputMasterId => _outputDevices.FirstOrDefault(d => d.IsMasterDevice)?.Info.Id;
    public string? FlaggedInputMasterId => _inputDevices.FirstOrDefault(d => d.IsMasterDevice)?.Info.Id;

    public ActiveDevice? GetInputMasterDevice() =>
        _inputDevices.FirstOrDefault(d => d.IsMasterDevice) ??
        _inputDevices.FirstOrDefault();

    /// <summary>
    /// The output master for RUNTIME purposes (fill reference, metrics consumer). While
    /// running, a device that is parked (no routes, hence no provider) cannot serve as
    /// one, so a live device wins — the flag itself keeps carrying the user's choice for
    /// the badge. Not running: the flag is the answer.
    /// </summary>
    public ActiveDevice? GetOutputMasterDevice()
    {
        if (_running)
        {
            var liveMaster = _outputDevices.FirstOrDefault(d => d.IsMasterDevice && d.MixProvider != null);
            if (liveMaster != null) return liveMaster;

            var coordinatorMasterId = _syncCoordinator?.GetMasterConsumerId();
            if (!string.IsNullOrWhiteSpace(coordinatorMasterId))
            {
                var coordinatorMaster = _outputDevices.FirstOrDefault(
                    d => d.MixProvider != null && d.ConsumerId == coordinatorMasterId);
                if (coordinatorMaster != null) return coordinatorMaster;
            }

            var anyLive = _outputDevices.FirstOrDefault(d => d.MixProvider != null);
            if (anyLive != null) return anyLive;
        }

        return _outputDevices.FirstOrDefault(d => d.IsMasterDevice) ??
               _outputDevices.FirstOrDefault();
    }

    /// <summary>
    /// Master to try first on a fresh Start(): the user's durable preference wins whenever
    /// that device is currently attached, then the session flag, then any candidate.
    /// <paramref name="eligibleIds"/> restricts the choice to the outputs that will
    /// actually start (the routed set); null or empty means no restriction.
    /// </summary>
    private ActiveDevice? ResolveStartMaster(HashSet<string>? eligibleIds = null)
    {
        bool Eligible(ActiveDevice d) =>
            eligibleIds is null || eligibleIds.Count == 0 || eligibleIds.Contains(d.Info.Id);

        if (!string.IsNullOrWhiteSpace(_preferredOutputMasterId))
        {
            var preferred = _outputDevices.FirstOrDefault(
                d => d.Info.Id == _preferredOutputMasterId && Eligible(d));
            if (preferred != null) return preferred;
        }
        return _outputDevices.FirstOrDefault(d => d.IsMasterDevice && Eligible(d))
               ?? _outputDevices.FirstOrDefault(Eligible);
    }

    /// <summary>Output device ids that carry at least one active crosspoint — the set
    /// that actually participates in a start (everything else is parked).</summary>
    private HashSet<string> GetRoutedOutputIdsFromMatrix()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var front = _routingMatrix.GetFrontBuffer();
        int outChannels = _routingMatrix.OutputChannels;
        if (front.Length == 0 || outChannels == 0) return ids;

        foreach (var dev in _outputDevices)
        {
            int start = dev.GlobalChannelOffset;
            int end = Math.Min(outChannels, start + dev.Info.Channels);
            bool routed = false;
            for (int outCh = start; outCh < end && !routed; outCh++)
            {
                for (int inCh = 0; inCh < _routingMatrix.InputChannels; inCh++)
                {
                    int idx = inCh * outChannels + outCh;
                    if (idx >= 0 && idx < front.Length && front[idx].Active)
                    {
                        routed = true;
                        break;
                    }
                }
            }
            if (routed) ids.Add(dev.Info.Id);
        }
        return ids;
    }

    /// <summary>
    /// A route change that adds a first route to a parked output (or removes the last
    /// route from a running one) changes which devices participate in the stream —
    /// restart through the normal barrier path, exactly like a device add/remove.
    /// </summary>
    private void RestartIfOutputParticipationChanged(HashSet<string> before)
    {
        if (!_running) return;
        var after = GetRoutedOutputIdsFromMatrix();
        if (before.SetEquals(after)) return;

        if (_deviceBatchDepth > 0)
        {
            Stop();
            _batchRestart = true;
        }
        else
        {
            FullRestart();
        }
    }

    /// <summary>
    /// Batches device add/remove calls into a single engine restart. While the returned
    /// scope is alive, Add/Remove skip their per-call Stop/Start; disposing the scope
    /// performs at most one Start() if the engine was running when the batch stopped it.
    /// </summary>
    public IDisposable BeginDeviceBatch()
    {
        _deviceBatchDepth++;
        return new DeviceBatchScope(this);
    }

    private sealed class DeviceBatchScope(AudioEngine engine) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            engine.EndDeviceBatch();
        }
    }

    private void EndDeviceBatch()
    {
        if (_deviceBatchDepth <= 0) return;
        if (--_deviceBatchDepth > 0) return;

        bool restart = _batchRestart;
        _batchRestart = false;
        if (restart && !_running
            && _inputDevices.Count > 0 && _outputDevices.Count > 0
            && _routingMatrix.HasAnyCrosspoints())
        {
            Start();
        }
    }

    public List<DeviceInfo> GetAvailableDevices(DataFlow flow) => _enumerator.GetDevices(flow);

    /// <summary>
    /// Returns DeviceInfo entries usable as capture inputs.
    /// </summary>
    public List<DeviceInfo> GetAvailableInputDevices(bool includeCapture, bool includeLoopback)
    {
        var list = new List<DeviceInfo>();
        if (includeCapture)
        {
            list.AddRange(_enumerator.GetDevices(DataFlow.Capture));
        }
        if (includeLoopback)
        {
            var renders = _enumerator.GetDevices(DataFlow.Render);
            foreach (var render in renders)
            {
                list.Add(new DeviceInfo(
                    $"loop:{render.Id}",
                    $"{render.Name} (loopback)",
                    render.Channels,
                    render.SampleRate,
                    DataFlow.Capture
                ));
            }
        }
        return list;
    }

    public bool AddInputDevice(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return false;
        if (_inputDevices.Any(d => d.Info.Id == deviceId)) return false;

        DeviceInfo? found;
        bool isLoopback = false;
        if (deviceId.StartsWith("loop:", StringComparison.Ordinal))
        {
            var renderId = deviceId.Substring("loop:".Length);
            var renderDevices = _enumerator.GetDevices(DataFlow.Render);
            var render = renderDevices.FirstOrDefault(d => d.Id == renderId);
            if (render == null) return false;
            found = new DeviceInfo(
                deviceId,
                $"{render.Name} (loopback)",
                render.Channels,
                render.SampleRate,
                DataFlow.Capture
            );
            isLoopback = true;
        }
        else
        {
            var devices = _enumerator.GetDevices(DataFlow.Capture);
            found = devices.FirstOrDefault(d => d.Id == deviceId);
        }
        if (found == null) return false;

        var ad = new ActiveDevice { Info = found, IsLoopback = isLoopback };
        bool wasRunning = _running;
        if (wasRunning) Stop();
        if (wasRunning && _deviceBatchDepth > 0) _batchRestart = true;
        _inputDevices.Add(ad);
        RecalcChannelOffsets();

        RestoreDormantRoutesForInputDevice(deviceId);

        // Promote the durable input-master preference the moment its device appears.
        if (found.Id == _preferredInputMasterId)
        {
            foreach (var d in _inputDevices)
            {
                d.IsMasterDevice = d.Info.Id == found.Id;
            }
        }

        if (_deviceBatchDepth == 0 && wasRunning && _inputDevices.Count > 0 && _outputDevices.Count > 0 && _routingMatrix.HasAnyCrosspoints()) Start();

        StateChanged?.Invoke();
        return true;
    }

    public bool AddOutputDevice(string deviceId)
    {
        if (_outputDevices.Any(d => d.Info.Id == deviceId)) return false;

        var devices = _enumerator.GetDevices(DataFlow.Render);
        var found = devices.FirstOrDefault(d => d.Id == deviceId);
        if (found == null) return false;

        var ad = new ActiveDevice
        {
            Info = found,
        };
        bool wasRunning = _running;
        if (wasRunning) Stop();
        if (wasRunning && _deviceBatchDepth > 0) _batchRestart = true;
        _outputDevices.Add(ad);
        RecalcChannelOffsets();

        RestoreDormantRoutesForOutputDevice(deviceId);

        // Promote the durable master preference the moment its device reappears.
        if (found.Id == _preferredOutputMasterId)
        {
            foreach (var d in _outputDevices)
            {
                d.IsMasterDevice = d.Info.Id == found.Id;
            }
        }

        if (_deviceBatchDepth == 0 && wasRunning && _inputDevices.Count > 0 && _outputDevices.Count > 0 && _routingMatrix.HasAnyCrosspoints()) Start();

        StateChanged?.Invoke();
        return true;
    }

    private void RestoreDormantRoutesForInputDevice(string inputDeviceId)
    {
        // Re-establish any saved routes for this input whose paired output is currently active.
        // Routes that pair with a still-disconnected output stay dormant.
        var inDev = _inputDevices.FirstOrDefault(d => d.Info.Id == inputDeviceId);
        if (inDev == null) return;

        var restored = new List<RoutedCrosspoint>();
        var updates = new List<(int InCh, int OutCh, bool Active, float GainDb, bool PhaseInverted)>();
        foreach (var dormant in _dormantRoutes.Where(r => r.InputDeviceId == inputDeviceId).ToList())
        {
            var outDev = _outputDevices.FirstOrDefault(d => d.Info.Id == dormant.OutputDeviceId);
            if (outDev == null) continue;
            if (dormant.InputLocalChannel < 0 || dormant.InputLocalChannel >= inDev.Info.Channels) continue;
            if (dormant.OutputLocalChannel < 0 || dormant.OutputLocalChannel >= outDev.Info.Channels) continue;

            int inGlobal = inDev.GlobalChannelOffset + dormant.InputLocalChannel;
            int outGlobal = outDev.GlobalChannelOffset + dormant.OutputLocalChannel;
            updates.Add((inGlobal, outGlobal, dormant.Active, dormant.GainDb, dormant.PhaseInverted));
            restored.Add(dormant);
        }

        if (restored.Count > 0)
        {
            foreach (var r in restored) _dormantRoutes.Remove(r);
            _routingMatrix.SetCrosspoints(updates);
        }
    }

    private void RestoreDormantRoutesForOutputDevice(string outputDeviceId)
    {
        var outDev = _outputDevices.FirstOrDefault(d => d.Info.Id == outputDeviceId);
        if (outDev == null) return;

        var restored = new List<RoutedCrosspoint>();
        var updates = new List<(int InCh, int OutCh, bool Active, float GainDb, bool PhaseInverted)>();
        foreach (var dormant in _dormantRoutes.Where(r => r.OutputDeviceId == outputDeviceId).ToList())
        {
            var inDev = _inputDevices.FirstOrDefault(d => d.Info.Id == dormant.InputDeviceId);
            if (inDev == null) continue;
            if (dormant.InputLocalChannel < 0 || dormant.InputLocalChannel >= inDev.Info.Channels) continue;
            if (dormant.OutputLocalChannel < 0 || dormant.OutputLocalChannel >= outDev.Info.Channels) continue;

            int inGlobal = inDev.GlobalChannelOffset + dormant.InputLocalChannel;
            int outGlobal = outDev.GlobalChannelOffset + dormant.OutputLocalChannel;
            updates.Add((inGlobal, outGlobal, dormant.Active, dormant.GainDb, dormant.PhaseInverted));
            restored.Add(dormant);
        }

        if (restored.Count > 0)
        {
            foreach (var r in restored) _dormantRoutes.Remove(r);
            _routingMatrix.SetCrosspoints(updates);
        }
    }

    /// <summary>
    /// Seeds saved routes into the dormant list (deduplicated on the device/channel
    /// 4-tuple). Called on config load so devices that are offline at launch can be
    /// re-attached and re-routed by the hotplug refresh when they appear later.
    /// </summary>
    public void SeedDormantRoutes(IEnumerable<RoutedCrosspoint> routes)
    {
        foreach (var route in routes)
        {
            if (string.IsNullOrWhiteSpace(route.InputDeviceId) || string.IsNullOrWhiteSpace(route.OutputDeviceId)) continue;

            int existing = _dormantRoutes.FindIndex(r =>
                r.InputDeviceId == route.InputDeviceId &&
                r.InputLocalChannel == route.InputLocalChannel &&
                r.OutputDeviceId == route.OutputDeviceId &&
                r.OutputLocalChannel == route.OutputLocalChannel);
            if (existing >= 0)
            {
                _dormantRoutes[existing] = route;
            }
            else
            {
                _dormantRoutes.Add(route);
            }
        }
    }

    /// <summary>
    /// Forgets every dormant route touching a device. This is the explicit-user-deletion
    /// path — hotplug removals keep their routes dormant for later restoration.
    /// </summary>
    public void RemoveDormantRoutesFor(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return;
        _dormantRoutes.RemoveAll(r => r.InputDeviceId == deviceId || r.OutputDeviceId == deviceId);
    }

    public bool RemoveInputDevice(string deviceId)
    {
        int index = _inputDevices.FindIndex(d => d.Info.Id == deviceId);
        if (index < 0) return false;
        RemoveInputDevice(index);
        return true;
    }

    public bool RemoveOutputDevice(string deviceId)
    {
        int index = _outputDevices.FindIndex(d => d.Info.Id == deviceId);
        if (index < 0) return false;
        RemoveOutputDevice(index);
        return true;
    }

    public void RemoveInputDevice(int index)
    {
        if (index < 0 || index >= _inputDevices.Count) return;

        var deviceToRemove = _inputDevices[index];
        CaptureRoutesForRemovedDevices(new List<ActiveDevice> { deviceToRemove }, new List<ActiveDevice>());

        var routeSnapshot = CaptureRoutedCrosspoints();
        bool wasRunning = _running;
        if (wasRunning) Stop();
        if (wasRunning && _deviceBatchDepth > 0) _batchRestart = true;
        _inputDevices.RemoveAt(index);
        RecalcChannelOffsets();
        RestoreRoutedCrosspoints(routeSnapshot);
        if (_deviceBatchDepth == 0 && wasRunning && _inputDevices.Count > 0 && _outputDevices.Count > 0 && _routingMatrix.HasAnyCrosspoints()) Start();
        StateChanged?.Invoke();
    }

    public void RemoveOutputDevice(int index)
    {
        if (index < 0 || index >= _outputDevices.Count) return;

        var deviceToRemove = _outputDevices[index];
        CaptureRoutesForRemovedDevices(new List<ActiveDevice>(), new List<ActiveDevice> { deviceToRemove });

        var routeSnapshot = CaptureRoutedCrosspoints();
        bool wasRunning = _running;
        if (wasRunning) Stop();
        if (wasRunning && _deviceBatchDepth > 0) _batchRestart = true;
        _outputDevices.RemoveAt(index);
        RecalcChannelOffsets();
        RestoreRoutedCrosspoints(routeSnapshot);
        if (_deviceBatchDepth == 0 && wasRunning && _inputDevices.Count > 0 && _outputDevices.Count > 0 && _routingMatrix.HasAnyCrosspoints()) Start();
        StateChanged?.Invoke();
    }

    public void SetCrosspoint(int inCh, int outCh, bool active, float gainDb, bool phaseInverted = false)
    {
        // Explicitly turning a route off is a user deletion — the dormant copy must go
        // too, or the route would silently resurrect on the next restart/replug.
        if (!active) PruneDormantRoute(inCh, outCh);

        // Only an Active flip can change which outputs participate; a gain/phase tweak
        // never can, so it must not pay for the matrix scan (the wheel fires these tens
        // of times a second), and neither must a stopped engine.
        bool mayChangeParticipation = _running && _routingMatrix.GetCrosspoint(inCh, outCh).Active != active;
        var routedBefore = mayChangeParticipation ? GetRoutedOutputIdsFromMatrix() : null;

        bool changed = _routingMatrix.SetCrosspoint(inCh, outCh, active, gainDb, phaseInverted);
        if (!changed)
        {
            return;
        }

        _routingMatrix.Publish();
        if (routedBefore is not null) RestartIfOutputParticipationChanged(routedBefore);
        StateChanged?.Invoke();
    }

    public (int Changed, int Skipped) SetCrosspoints(IEnumerable<(int InCh, int OutCh, bool Active, float GainDb, bool PhaseInverted)> updates)
    {
        var list = updates as IReadOnlyCollection<(int InCh, int OutCh, bool Active, float GainDb, bool PhaseInverted)> ?? updates.ToList();
        foreach (var update in list)
        {
            if (!update.Active) PruneDormantRoute(update.InCh, update.OutCh);
        }

        // Same rule as SetCrosspoint: scan only when an Active bit actually flips.
        bool mayChangeParticipation = false;
        if (_running)
        {
            foreach (var update in list)
            {
                if (_routingMatrix.GetCrosspoint(update.InCh, update.OutCh).Active != update.Active)
                {
                    mayChangeParticipation = true;
                    break;
                }
            }
        }
        var routedBefore = mayChangeParticipation ? GetRoutedOutputIdsFromMatrix() : null;

        var result = _routingMatrix.SetCrosspoints(list);
        if (result.Changed > 0)
        {
            if (routedBefore is not null) RestartIfOutputParticipationChanged(routedBefore);
            StateChanged?.Invoke();
        }

        return result;
    }

    private void PruneDormantRoute(int inCh, int outCh)
    {
        if (_dormantRoutes.Count == 0) return;

        var inDev = FindInputDeviceByChannel(inCh);
        var outDev = FindOutputDeviceByChannel(outCh);
        if (inDev == null || outDev == null) return;

        int inLocal = inCh - inDev.GlobalChannelOffset;
        int outLocal = outCh - outDev.GlobalChannelOffset;
        _dormantRoutes.RemoveAll(r =>
            r.InputDeviceId == inDev.Info.Id && r.InputLocalChannel == inLocal &&
            r.OutputDeviceId == outDev.Info.Id && r.OutputLocalChannel == outLocal);
    }

    private ActiveDevice? FindInputDeviceByChannel(int inCh)
    {
        return _inputDevices.FirstOrDefault(d =>
            inCh >= d.GlobalChannelOffset && inCh < d.GlobalChannelOffset + d.Info.Channels);
    }

    private ActiveDevice? FindOutputDeviceByChannel(int outCh)
    {
        return _outputDevices.FirstOrDefault(d =>
            outCh >= d.GlobalChannelOffset && outCh < d.GlobalChannelOffset + d.Info.Channels);
    }

    public void ClearCrosspoints()
    {
        // "Clear routes" means everything, including routes waiting on offline devices —
        // otherwise they resurrect on the next restart.
        _dormantRoutes.Clear();
        _routingMatrix.ClearAll();
        StateChanged?.Invoke();
    }

    public bool Start() => StartCore(null, 0);

    private bool StartCore(ActiveDevice? masterOverride, int attempt, bool allowExclusive = true)
    {
        if (_running) return true;
        if (_inputDevices.Count == 0 || _outputDevices.Count == 0) return false;

        try
        {
            // ===== 0. Outputs with no active route are PARKED: no render endpoint, no
            // ring consumer, no coordinator entry. A parked virtual sink (Steam streaming
            // speakers idle, a sleeping HDMI monitor…) used to be a full engine citizen:
            // its stalling render callback let every input ring overflow past capacity
            // (millions of "dropped frames" in bursts) and its oversized real buffer set
            // the worst-gulp fill floor for ALL inputs, inflating reported input latency
            // far above the knob. Routing to a parked output restarts the engine (see
            // SetCrosspoints), exactly like adding a device does.
            var routedOutputIds = GetRoutedOutputIdsFromMatrix();
            bool ParticipatesInStart(ActiveDevice d) =>
                routedOutputIds.Count == 0 || routedOutputIds.Contains(d.Info.Id);

            // The CLOCK master must be a device that actually starts, so it is chosen
            // among the routed outputs. The IsMasterDevice flags are deliberately NOT
            // rewritten here: they carry the user's choice (that is what the MASTER
            // badge shows, and what keeps a chosen row visible with show-all off).
            // Runtime plumbing asks the coordinator or GetOutputMasterDevice(), both of
            // which resolve to a live device.
            var masterOutput = masterOverride
                ?? ResolveStartMaster(routedOutputIds)
                ?? _outputDevices.First();

            // ===== 1. Engine clock = master output's nominal rate =====
            _engineSampleRate = masterOutput.Info.SampleRate > 0 ? masterOutput.Info.SampleRate : 48000;

            // Diagnostic: log each endpoint's real shared-mode period capabilities
            // (%TEMP%\amr-wasapi-probe.log) — once per endpoint per process (each probe
            // is a COM activation, and this used to run on every restart).
            try
            {
                Wasapi.WasapiDiagnostics.ProbeEnginePeriodsOnce("out " + masterOutput.Info.Name, masterOutput.Info.Id);
                var probeMic = _inputDevices.FirstOrDefault(d => !d.IsLoopback);
                if (probeMic != null)
                    Wasapi.WasapiDiagnostics.ProbeEnginePeriodsOnce("in " + probeMic.Info.Name, probeMic.Info.Id);
            }
            catch { }
            Wasapi.WasapiDiagnostics.Log($"[Engine] start #{++_startSequence} attempt {attempt}: master '{masterOutput.Info.Name}' " +
                $"engineRate {_engineSampleRate} knob {_outputBufferMs} in {_inputBufferMs} " +
                $"routedOutputs {routedOutputIds.Count} inputs {_inputDevices.Count} outputs {_outputDevices.Count}");

            // ===== 2. Allocate rings (engine-rate content) =====
            int ringFrames = Math.Max(_engineSampleRate * RingBufferCapacityMs / 1000, _engineSampleRate / 200);
            foreach (var dev in _inputDevices)
            {
                dev.RingBuffer = new RingBuffer(ringFrames, dev.Info.Channels);
                dev.InputOverflowCount = 0;
                dev.PeakLevels = new float[dev.Info.Channels];
                dev.InputAsrc = null; // built when the capture is created
            }

            // ===== 3. Coordinator =====
            ResetAutoWindow();
            var (baseMasterTargetFrames, maxMasterTargetFrames) = CalculateSyncTargetFrames();
            _syncCoordinator = new OutputSyncCoordinator(
                masterOutput.Info.Id, _engineSampleRate, baseMasterTargetFrames, maxMasterTargetFrames);
            _syncCoordinator.ArmGlobalRefillHold();
            var coordinatorForEvent = _syncCoordinator;
            _syncCoordinator.MasterChanged += newMasterId =>
            {
                // Audio thread. Cheap work only: re-point the rings' preferred consumer
                // (the servos already resolve the fill reference per update) and log.
                // The old master, if it is merely asleep, will fault through the stall
                // detector and the host handles recovery from there.
                if (!ReferenceEquals(coordinatorForEvent, _syncCoordinator)) return;
                var dev = _outputDevices.FirstOrDefault(d => d.ConsumerId == newMasterId);
                Wasapi.WasapiDiagnostics.Log($"[Engine] master STALLED — promoted '{dev?.Info.Name ?? newMasterId}' as live clock master");
                foreach (var input in _inputDevices)
                {
                    input.RingBuffer?.SetPreferredConsumer(newMasterId);
                }
                try { StateChanged?.Invoke(); } catch { }
            };

            var sources = _inputDevices
                .Where(d => d.RingBuffer != null)
                .Select(d => new MixingSampleProvider.CaptureSource(
                    d.Info.Id,
                    d.RingBuffer!,
                    d.GlobalChannelOffset,
                    d.Info.Channels,
                    d.IsMasterDevice))
                .ToList();

            // Exclusive eligibility. Outputs that feed a loopback input must stay shared:
            // an exclusive stream bypasses the engine the loopback taps (it would go
            // silent). All-or-nothing across outputs (equal FIFO depth, see the field).
            var loopbackSourceIds = new HashSet<string>(
                _inputDevices.Where(d => d.IsLoopback)
                    .Select(d => d.Info.Id.StartsWith("loop:", StringComparison.Ordinal) ? d.Info.Id.Substring(5) : d.Info.Id),
                StringComparer.Ordinal);
            bool wantExclusive = allowExclusive && _exclusiveMode && !_exclusiveDemoted;
            _exclusiveRenderActive = false;
            ExclusiveBlockedReason = !_exclusiveMode ? string.Empty
                : _exclusiveDemoted ? "an output could not sustain its exclusive period this session"
                : !allowExclusive ? (_pendingExclusiveBlock ?? "exclusive unavailable")
                : string.Empty;

            // ===== 4. Providers + renders (consumer cursors pinned on EMPTY rings) =====
            var startedOutputs = new List<ActiveDevice>();
            bool formatChanged = false;
            foreach (var dev in _outputDevices)
            {
                if (!ParticipatesInStart(dev))
                {
                    // Parked: clear stale endpoint read-backs so metrics report nothing.
                    dev.RealRenderBufferMs = 0;
                    dev.RenderPeriodMs = 0;
                    dev.RenderLatencyMs = 0;
                    continue;
                }

                var mmDevice = _enumerator.GetDevice(dev.Info.Id);
                if (mmDevice == null) continue;

                dev.ConsumerId = dev.Info.Id;
                dev.MixProvider = new MixingSampleProvider(
                    _routingMatrix, sources,
                    dev.GlobalChannelOffset,
                    dev.Info.Channels,
                    dev.Info.SampleRate,
                    dev.OutputDelayMs,
                    _outputBufferMs,
                    dev.ConsumerId,
                    _syncCoordinator);

                bool exclusiveForThisOutput = wantExclusive && !loopbackSourceIds.Contains(dev.Info.Id);
                if (!exclusiveForThisOutput && wantExclusive) dev.ExclusiveRefusal = "is a loopback source";
                if (!TryInitRender(dev, mmDevice, RenderBufferRequestMs, exclusiveForThisOutput))
                {
                    try { dev.MixProvider?.DetachConsumer(); } catch { }
                    dev.MixProvider = null;
                    dev.ConsumerId = string.Empty;
                    continue; // Skip this device; engine continues with the remaining outputs.
                }

                // Render format guard: the custom client opened the endpoint at its LIVE
                // mix format. If that differs from what we enumerated (HDMI sinks
                // renegotiate on wake; the user edited Sound settings), the provider we
                // just built would feed 48 k content into a 44.1 k stream (pitch -8.8 %)
                // or mis-interleave a changed channel count (noise). Adopt the real
                // format and rebuild once around it.
                if (dev.Render is Wasapi.WasapiRenderClient wrc &&
                    wrc.ActualSampleRate > 0 && wrc.ActualChannels > 0 &&
                    (wrc.ActualSampleRate != dev.Info.SampleRate || wrc.ActualChannels != dev.Info.Channels))
                {
                    Wasapi.WasapiDiagnostics.Log($"[Engine] render format drift on '{dev.Info.Name}': " +
                        $"enumerated {dev.Info.SampleRate}Hz/{dev.Info.Channels}ch, live {wrc.ActualSampleRate}Hz/{wrc.ActualChannels}ch — rebuilding");
                    dev.Info = dev.Info with { SampleRate = wrc.ActualSampleRate, Channels = wrc.ActualChannels };
                    formatChanged = true;
                }

                startedOutputs.Add(dev);
            }

            if (formatChanged && attempt < 1)
            {
                // Rebuild around the real formats: channel offsets, matrix, rings, engine
                // rate (if the master changed) all derive from Info.
                var routeSnapshot = CaptureRoutedCrosspoints();
                Stop();
                RecalcChannelOffsets();
                RestoreRoutedCrosspoints(routeSnapshot);
                return StartCore(null, attempt + 1, allowExclusive);
            }

            // Startup is not valid without at least one active render endpoint.
            if (startedOutputs.Count == 0)
            {
                Wasapi.WasapiDiagnostics.Log("[Engine] start aborted: no render endpoint could be opened");
                Stop();
                return false;
            }

            // If the preferred master failed to initialise, promote a live output as runtime
            // master. When the promoted device's nominal rate differs from the engine rate
            // everything was built around, tear down and rebuild once around the promoted
            // master's clock so "engine clock = master clock, master trim = 0" stays true.
            if (!startedOutputs.Any(d => d.Info.Id == masterOutput.Info.Id))
            {
                var runtimeMaster = startedOutputs[0];
                foreach (var d in _outputDevices)
                {
                    d.IsMasterDevice = d.Info.Id == runtimeMaster.Info.Id;
                }

                if (runtimeMaster.Info.SampleRate != _engineSampleRate && attempt < 1)
                {
                    Stop();
                    return StartCore(runtimeMaster, attempt + 1, allowExclusive);
                }

                _syncCoordinator.SetMasterConsumer(runtimeMaster.Info.Id);
                _syncCoordinator.ArmGlobalRefillHold();
                masterOutput = runtimeMaster;
            }

            if (wantExclusive)
            {
                var notExclusive = startedOutputs.Where(d => d.Render?.Tier != EndpointTier.Exclusive).ToList();
                if (notExclusive.Count > 0)
                {
                    var first = notExclusive[0];
                    string why = $"'{first.Info.Name}' {(string.IsNullOrEmpty(first.ExclusiveRefusal) ? "could not open exclusive" : first.ExclusiveRefusal)}";
                    Wasapi.WasapiDiagnostics.Log($"[Engine] exclusive mode unavailable this start — {why}; reopening every output shared");
                    _pendingExclusiveBlock = why;
                    Stop();
                    return StartCore(masterOverride, 0, allowExclusive: false);
                }
                _exclusiveRenderActive = true;
                _pendingExclusiveBlock = null;
            }

            // Preferred consumer for ring trim ordering + the input ASRC fill reference.
            ApplyPreferredMasterConsumerToInputs();

            // Real render periods and buffers are known now: compute the budget, hand
            // every output the SAME padding target (equal FIFO depths — the sync
            // coordinator assumes that), and make the barrier release at exactly the
            // servo's fill target.
            _worstRenderPeriodMs = startedOutputs.Max(d => d.Render!.ActualPeriodMs > 0 ? d.Render.ActualPeriodMs : 10);
            _minRealRenderBufferMs = startedOutputs.Min(d => d.RealRenderBufferMs > 0 ? d.RealRenderBufferMs : int.MaxValue);
            if (_minRealRenderBufferMs == int.MaxValue) _minRealRenderBufferMs = 0;
            _budget = ComputeBudget();
            if (_budget.NeedsReallocation && attempt < 1)
            {
                // The padding this budget needs (safety tier / a big knob) does not fit
                // the buffers we were given; reopen once with the request sized from
                // the now-known period.
                Wasapi.WasapiDiagnostics.Log($"[Engine] render buffers too small for padding {_budget.RenderPaddingMs} ms — reopening");
                Stop();
                return StartCore(masterOutput, attempt + 1, allowExclusive);
            }
            ApplyRenderPadding(startedOutputs);
            _syncCoordinator.SetReleaseFillTargetMs(InputFillTargetMs);

            // ===== 5. Start captures (rings begin filling; cursors already pinned) =====
            _worstCaptureCadenceMs = 10; // provisional until real cadences are known
            _budget = ComputeBudget();
            int inputFillTargetFrames = CalculateInputFillTargetFrames();
            foreach (var dev in _inputDevices)
            {
                if (dev.RingBuffer == null) continue;
                bool exclusiveCapture = _exclusiveMode && !dev.IsLoopback && !dev.ExclusiveCaptureDemoted;
                if (!CreateAndStartCapture(dev, inputFillTargetFrames, masterOutput.Info.Id, exclusiveCapture))
                {
                    continue;
                }
            }

            // Real cadences are known now (small-period mics can be < 10 ms, exclusive
            // ones 3 ms): recompute the budget and give every input ITS OWN fill target
            // + retarget the barrier BEFORE release — the rings are still pre-filling,
            // so this is glitch-free.
            _worstCaptureCadenceMs = _inputDevices
                .Where(d => d.Capture != null)
                .Select(d => d.EffectiveCadenceMs > 0 ? d.EffectiveCadenceMs : d.CaptureCadenceMs)
                .DefaultIfEmpty(10)
                .Max();
            _budget = ComputeBudget();
            ApplyInputFillTargets(_inputDevices);
            _syncCoordinator.SetReleaseFillTargetMs(InputFillTargetMs);

            LogBudget("start", startedOutputs);

            // ===== 6. Play all outputs together =====
            foreach (var dev in _outputDevices)
            {
                try { dev.Render?.Play(); } catch { }
            }

            _running = true;
            _runningSince = Environment.TickCount64;
            StateChanged?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            Wasapi.WasapiDiagnostics.Log($"[Engine] start FAILED: {ex.GetType().Name}: {ex.Message}");
            Stop();
            return false;
        }
    }

    private int _startSequence;

    public bool SetOutputDelayMs(string deviceId, int delayMs)
    {
        var device = _outputDevices.FirstOrDefault(d => d.Info.Id == deviceId);
        if (device == null)
        {
            return false;
        }

        var clampedDelayMs = Math.Clamp(delayMs, 0, 5000);
        device.OutputDelayMs = clampedDelayMs;
        device.MixProvider?.SetDeviceDelayMs(clampedDelayMs);
        StateChanged?.Invoke();
        return true;
    }

    /// <summary>Kept for config compatibility; the budget no longer uses it.</summary>
    public bool SetInputBufferMs(int bufferMs)
    {
        _inputBufferMs = Math.Clamp(bufferMs, 10, 200);
        return true;
    }

    private bool TryInitRender(ActiveDevice dev, MMDevice mmDevice, int latencyMs, bool exclusiveWanted = false)
    {
        // "auto" backend: our IAudioClient3 client (small-period ladder inside it).
        // A total failure of the custom client falls through to NAudio — the belt
        // under the ladder's own braces.
        if (!string.Equals(_backend, "legacy", StringComparison.OrdinalIgnoreCase))
        {
            IRenderEndpoint? rc = null;
            try
            {
                rc = new Wasapi.WasapiRenderClient(
                    dev.Info.Id, latencyMs, dev.RequestedRenderTier, dev.Info.Name, exclusiveWanted);
                rc.Init(dev.MixProvider!);
                AdoptRender(dev, rc, latencyMs);
                if (rc is Wasapi.WasapiRenderClient wrcRefusal && exclusiveWanted && !wrcRefusal.IsExclusive)
                    dev.ExclusiveRefusal = string.IsNullOrEmpty(wrcRefusal.ExclusiveRefusal) ? "could not open exclusive" : wrcRefusal.ExclusiveRefusal;
                return true;
            }
            catch (Exception ex)
            {
                Wasapi.WasapiDiagnostics.Log($"[Engine] custom render open failed for '{dev.Info.Name}': {ex.Message}");
                // The client was never adopted: dispose it HERE or its wait handles
                // leak on every failed attempt (hundreds per hour in a fault storm).
                try { rc?.Dispose(); } catch { }
                try { dev.Render?.Dispose(); } catch { }
                dev.Render = null;
            }
        }

        IRenderEndpoint? render = null;
        try
        {
            render = new NAudioRenderEndpoint(mmDevice, latencyMs, dev.Info.SampleRate);
            render.Init(dev.MixProvider!);
            AdoptRender(dev, render, latencyMs);
            if (exclusiveWanted) dev.ExclusiveRefusal = "legacy backend (NAudio) cannot open exclusive";
            return true;
        }
        catch (Exception ex)
        {
            Wasapi.WasapiDiagnostics.Log($"[Engine] legacy render open failed for '{dev.Info.Name}': {ex.Message}");
            try { render?.Dispose(); } catch { }
            try { dev.Render?.Dispose(); } catch { }
            dev.Render = null;
            return false;
        }
    }

    private void AdoptRender(ActiveDevice dev, IRenderEndpoint render, int requestedMs)
    {
        dev.Render = render;
        render.Faulted += hr => OnEndpointFaulted(dev.Info.Name, hr);
        dev.TierUnderrunBaseline = 0;
        dev.HealthFaulted = false;
        dev.TierStartTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        // Honest numbers come from the endpoint (real buffer + one period of
        // mix-ahead); fall back to the request when read-back was unavailable.
        dev.RealRenderBufferMs = render.ActualBufferMs;
        dev.RenderPeriodMs = (int)Math.Ceiling(render.ActualPeriodMs);
        dev.RenderLatencyMs = render.ActualBufferMs > 0
            ? render.ActualBufferMs + dev.RenderPeriodMs
            : requestedMs;
    }

    private bool CreateAndStartCapture(ActiveDevice dev, int fillTargetFrames, string masterConsumerId,
        bool exclusiveWanted = false)
    {
        if (dev.RingBuffer == null)
        {
            return false;
        }
        // The ring's own target must exist before its producer writes a frame, or the
        // barrier (per-ring surplus) could release on an empty ring.
        dev.RingBuffer.FillTargetFrames = fillTargetFrames;
        dev.FillTargetFrames = fillTargetFrames;
        dev.ExclusiveRefusal = string.Empty;

        var mmDevice = _enumerator.GetDevice(dev.IsLoopback && dev.Info.Id.StartsWith("loop:", StringComparison.Ordinal)
            ? dev.Info.Id.Substring("loop:".Length)
            : dev.Info.Id);
        if (mmDevice == null)
        {
            return false;
        }

        // Endpoint buffer DEPTH is not latency — the delivery cadence drains it every
        // period regardless. A 1-period-deep buffer silently DROPPED a packet whenever
        // the capture thread was one period late; 4+ periods of depth costs nothing.
        // Loopbacks open event-driven with a keep-alive stream (polled is the fallback).
        string endpointId = dev.IsLoopback && dev.Info.Id.StartsWith("loop:", StringComparison.Ordinal)
            ? dev.Info.Id.Substring("loop:".Length)
            : dev.Info.Id;
        ICaptureEndpoint? custom = null;
        if (!string.Equals(_backend, "legacy", StringComparison.OrdinalIgnoreCase))
        {
            custom = new Wasapi.WasapiCaptureClient(
                endpointId,
                dev.IsLoopback
                    ? Wasapi.WasapiCaptureClient.CaptureMode.EventLoopback
                    : Wasapi.WasapiCaptureClient.CaptureMode.EventMic,
                dev.RequestedCaptureTier,
                dev.Info.Name,
                exclusiveWanted);
        }
        dev.Capture = custom ?? new NAudioCaptureEndpoint(mmDevice, dev.IsLoopback,
            Math.Max(40, _inputBufferMs), dev.Info.SampleRate, dev.Info.Channels);

        // Capture-side ASRC: convert this device's stream into the engine clock domain.
        // Handles BOTH static rate mismatch (e.g. 44.1k capture → 48k engine) and crystal
        // drift (fill-level PI), so the rings always contain coherent engine-rate audio.
        var asrc = new InputAsrc(dev.RingBuffer, dev.Info.Channels, dev.Info.SampleRate, _engineSampleRate, fillTargetFrames);
        // Fill reference = the coordinator's CURRENT master, resolved per update, so a
        // live master promotion (stalled HDMI master) moves every servo with it.
        var coordinator = _syncCoordinator;
        asrc.SetFillConsumer(() => coordinator?.GetMasterConsumerId() is { Length: > 0 } id ? id : masterConsumerId);
        dev.InputAsrc = asrc;

        RewireCaptureHandler(dev);

        try
        {
            dev.Capture.Start();

            // Format drift guard for the custom client: it captures at the LIVE mix
            // format; if that no longer matches what we enumerated (user changed the
            // endpoint format), the ASRC's static ratio would be wrong. Fall back to
            // the NAudio adapter, which converts to our requested format.
            if (dev.Capture is Wasapi.WasapiCaptureClient wc &&
                (wc.ActualSampleRate != dev.Info.SampleRate || wc.ActualChannels != dev.Info.Channels))
            {
                var handlerCarrier = dev.Capture;
                try { handlerCarrier.Dispose(); } catch { }
                dev.Capture = new NAudioCaptureEndpoint(mmDevice, dev.IsLoopback,
                    Math.Max(40, _inputBufferMs), dev.Info.SampleRate, dev.Info.Channels);
                RewireCaptureHandler(dev);
                dev.Capture.Start();
            }
        }
        catch
        {
            // Custom client failed outright → one NAudio retry before giving up.
            if (dev.Capture is Wasapi.WasapiCaptureClient)
            {
                try { dev.Capture?.Dispose(); } catch { }
                try
                {
                    dev.Capture = new NAudioCaptureEndpoint(mmDevice, dev.IsLoopback,
                        Math.Max(40, _inputBufferMs), dev.Info.SampleRate, dev.Info.Channels);
                    RewireCaptureHandler(dev);
                    dev.Capture.Start();
                }
                catch
                {
                    try { dev.Capture?.Dispose(); } catch { }
                    dev.Capture = null;
                    dev.InputAsrc = null;
                    return false;
                }
            }
            else
            {
                try { dev.Capture?.Dispose(); } catch { }
                dev.Capture = null;
                dev.InputAsrc = null;
                return false;
            }
        }

        // Latency contribution = delivery cadence (one period), NOT buffer depth. The
        // nominal period seeds the effective cadence; the health tick raises it to the
        // measured recurring gap once the producer has shown its real behaviour.
        dev.CaptureCadenceMs = dev.Capture.ActualPeriodMs;
        // Loopback prior: virtual cables deliver in bursts of roughly twice the period;
        // start there and let the first measurement settle the real value (within ~1 s).
        dev.EffectiveCadenceMs = dev.IsLoopback ? 2 * dev.CaptureCadenceMs : dev.CaptureCadenceMs;
        dev.CadenceIsPrior = dev.IsLoopback;
        dev.CaptureLatencyMs = (int)Math.Ceiling(dev.EffectiveCadenceMs);
        asrc.SetCadenceMs(dev.CaptureCadenceMs);
        dev.RingBuffer.ProducerCadenceMs = dev.CaptureCadenceMs;
        if (dev.Capture is Wasapi.WasapiCaptureClient exclusiveClient && exclusiveWanted && exclusiveClient.Tier != EndpointTier.Exclusive)
            dev.ExclusiveRefusal = string.IsNullOrEmpty(exclusiveClient.ExclusiveRefusal) ? "could not open exclusive" : exclusiveClient.ExclusiveRefusal;
        dev.Capture.Faulted += hr => OnEndpointFaulted(dev.Info.Name, hr);
        dev.TierDiscontinuityBaseline = 0;
        return true;
    }

    /// <summary>Re-attaches the standard capture handler after a fallback swap.</summary>
    private void RewireCaptureHandler(ActiveDevice dev)
    {
        int channels = dev.Info.Channels;
        dev.Capture!.DataAvailable += (buffer, frames) =>
        {
            var deviceAsrc = dev.InputAsrc;
            if (dev.RingBuffer == null || deviceAsrc == null || frames <= 0) return;

            var peaks = dev.PeakLevels;
            if (peaks != null)
            {
                for (int f = 0; f < frames; f++)
                {
                    int baseIdx = f * channels;
                    for (int c = 0; c < channels; c++)
                    {
                        float v = buffer[baseIdx + c];
                        if (v < 0) v = -v;
                        if (v > peaks[c]) peaks[c] = v;
                    }
                }
            }

            int written = deviceAsrc.ProcessAndWrite(buffer, frames);
            if (written <= 0 && frames > 0)
            {
                Interlocked.Increment(ref dev.InputOverflowCount);
            }
        };
    }

    /// <summary>The LATENCY knob: 0 = AUTO, else the end-to-end target in ms. Applied
    /// live when running (no restart unless the render buffers must be reallocated).</summary>
    public bool SetOutputBufferMs(int bufferMs)
    {
        int clamped = Math.Clamp(bufferMs, LatencyBudget.AutoKnob, LatencyBudget.MaxKnobMs);
        if (_outputBufferMs == clamped)
        {
            return true;
        }

        _outputBufferMs = clamped;
        if (_running)
        {
            ApplyBudgetLive(clamped == LatencyBudget.AutoKnob ? "knob → AUTO" : $"knob → {clamped} ms");
        }

        StateChanged?.Invoke();
        return true;
    }

    private void FullRestart()
    {
        Stop();
        Start();
    }

    public void Stop()
    {
        foreach (var dev in _inputDevices)
        {
            try { dev.Capture?.Stop(); } catch { }
            try { dev.Capture?.Dispose(); } catch { }
            dev.Capture = null;
            dev.InputAsrc = null;
            dev.RingBuffer?.Clear();
        }

        foreach (var dev in _outputDevices)
        {
            try { dev.Render?.Stop(); } catch { }
            try { dev.Render?.Dispose(); } catch { }
            dev.Render = null;
            try { dev.MixProvider?.DetachConsumer(); } catch { }
            dev.MixProvider = null;
            dev.ConsumerId = string.Empty;
        }

        _syncCoordinator = null;

        _running = false;
        StateChanged?.Invoke();
    }

    private void RecalcChannelOffsets()
    {
        TotalInputChannels = 0;
        foreach (var d in _inputDevices)
        {
            d.GlobalChannelOffset = TotalInputChannels;
            TotalInputChannels += d.Info.Channels;
        }

        TotalOutputChannels = 0;
        foreach (var d in _outputDevices)
        {
            d.GlobalChannelOffset = TotalOutputChannels;
            TotalOutputChannels += d.Info.Channels;
        }

        _routingMatrix.Resize(TotalInputChannels, TotalOutputChannels);
        _routingMatrix.Publish();
    }

    private void ApplyPreferredMasterConsumerToInputs()
    {
        // The fill reference must be a consumer that actually exists on the rings — the
        // coordinator's master is authoritative (a flagged-but-parked output would hand
        // the servos a cursor nobody advances, which reads as ever-growing fill).
        var preferredConsumerId = _syncCoordinator?.GetMasterConsumerId();
        if (string.IsNullOrWhiteSpace(preferredConsumerId))
        {
            var masterOutput = GetOutputMasterDevice() ?? _outputDevices.FirstOrDefault();
            if (masterOutput == null) return;
            preferredConsumerId = string.IsNullOrWhiteSpace(masterOutput.ConsumerId)
                ? masterOutput.Info.Id
                : masterOutput.ConsumerId;
        }

        var coordinator = _syncCoordinator;
        var fallback = preferredConsumerId;
        foreach (var input in _inputDevices)
        {
            input.RingBuffer?.SetPreferredConsumer(preferredConsumerId);
            input.InputAsrc?.SetFillConsumer(() => coordinator?.GetMasterConsumerId() is { Length: > 0 } id ? id : fallback);
        }
    }

    // ===================================================================== latency budget
    // See LatencyBudget: everything derives from the measured render period, real
    // render buffer and capture cadence, plus the knob and the AUTO loop's headroom.

    private double _worstRenderPeriodMs = 10;   // worst render engine period across started outputs
    private int _minRealRenderBufferMs;          // smallest real render allocation (0 until opened)
    private double _worstCaptureCadenceMs = 10;  // worst capture delivery cadence across started inputs
    private int _autoHeadroomMs;                 // AUTO loop: extra ring fill because of underruns
    private int _safetyPeriods;                  // AUTO loop: extra render periods of padding
    private LatencyBudgetResult _budget;

    /// <summary>The budget currently applied (for metrics / UI).</summary>
    public LatencyBudgetResult CurrentBudget => _budget;
    public int AutoHeadroomMs => _autoHeadroomMs;
    public bool SafetyTier => _safetyPeriods > 0;

    private int RenderBufferRequestMs => LatencyBudget.RenderBufferRequestMs(_outputBufferMs, _worstRenderPeriodMs);

    private LatencyBudgetInput BudgetInput() => new(
        _outputBufferMs, _worstCaptureCadenceMs, _worstRenderPeriodMs, _minRealRenderBufferMs,
        _autoHeadroomMs, _safetyPeriods, _exclusiveRenderActive);

    private LatencyBudgetResult ComputeBudget() => LatencyBudget.Compute(BudgetInput());

    private int InputFillTargetMs => Math.Max(1, _budget.RingTargetMs);

    private int CalculateInputFillTargetFrames() =>
        Math.Max(64, _engineSampleRate * InputFillTargetMs / 1000);

    /// <summary>This input's own ring target from its own cadence (worst-input budget otherwise).</summary>
    private int FillTargetFramesFor(ActiveDevice input)
    {
        double cadence = input.EffectiveCadenceMs > 0 ? input.EffectiveCadenceMs : _worstCaptureCadenceMs;
        int ms = LatencyBudget.RingTargetForCadenceMs(BudgetInput(), _budget, cadence);
        return Math.Max(64, _engineSampleRate * Math.Max(1, ms) / 1000);
    }

    private void ApplyInputFillTargets(IEnumerable<ActiveDevice> inputs)
    {
        foreach (var input in inputs)
        {
            if (input.RingBuffer is null) continue;
            int frames = FillTargetFramesFor(input);
            input.FillTargetFrames = frames;
            input.RingBuffer.FillTargetFrames = frames;
            input.InputAsrc?.SetTargetFillFrames(frames);
        }
    }

    private void ApplyRenderPadding(IEnumerable<ActiveDevice> outputs)
    {
        foreach (var dev in outputs)
        {
            if (dev.Render is null) continue;
            dev.Render.SetTargetPaddingMs(_budget.RenderPaddingMs);
            dev.RenderLatencyMs = dev.Render.FifoMs + dev.RenderPeriodMs;
        }
    }

    /// <summary>
    /// Re-targets a RUNNING engine to the current knob/headroom without a restart:
    /// render padding, every input servo's fill target and the barrier release
    /// target move in place. A ring-target decrease is one equal backlog trim on
    /// every ring (one small skip, outputs stay aligned); an increase is one equal
    /// hold on every output (one small gap). Only a padding that no longer fits the
    /// allocated render buffers forces a reopen.
    /// </summary>
    private void ApplyBudgetLive(string reason)
    {
        if (!_running || _syncCoordinator is null) return;
        var old = _budget;
        var next = ComputeBudget();
        if (next.NeedsReallocation)
        {
            Wasapi.WasapiDiagnostics.Log($"[Engine] retarget ({reason}): padding {next.RenderPaddingMs} ms exceeds the render buffers — restarting");
            FullRestart();
            return;
        }
        _budget = next;

        var outputs = _outputDevices.ToArray();
        var inputs = _inputDevices.ToArray();
        ApplyRenderPadding(outputs);

        // Per input: its own new target; a decrease is a backlog trim on THAT ring, an
        // increase is one equal hold on every output sized to the largest increase
        // (rings with a smaller increase overshoot by the difference, which the servo
        // removes gently — inaudible, and far cheaper than unequal holds breaking phase).
        int thresholdFrames = 5 * _engineSampleRate / 1000;
        int maxIncreaseFrames = 0;
        foreach (var input in inputs)
        {
            if (input.RingBuffer is null) continue;
            int oldFrames = input.FillTargetFrames;
            int newFrames = FillTargetFramesFor(input);
            input.FillTargetFrames = newFrames;
            input.RingBuffer.FillTargetFrames = newFrames;
            input.InputAsrc?.SetTargetFillFrames(newFrames);
            int deltaFrames = newFrames - oldFrames;
            if (oldFrames > 0 && deltaFrames <= -thresholdFrames)
            {
                input.RingBuffer.TrimBacklogBy(-deltaFrames);
            }
            else if (oldFrames > 0 && deltaFrames >= thresholdFrames)
            {
                maxIncreaseFrames = Math.Max(maxIncreaseFrames, deltaFrames);
            }
        }
        _syncCoordinator.SetReleaseFillTargetMs(InputFillTargetMs);
        if (maxIncreaseFrames > 0)
        {
            foreach (var output in outputs) output.MixProvider?.HoldFor(maxIncreaseFrames);
        }
        _ = old;

        LogBudget($"retarget: {reason}", outputs.Where(d => d.Render != null));
        try { StateChanged?.Invoke(); } catch { }
    }

    private void LogBudget(string what, IEnumerable<ActiveDevice> started)
    {
        var b = _budget;
        Wasapi.WasapiDiagnostics.Log($"[Engine] budget ({what}): knob {(b.IsAuto ? "AUTO" : _outputBufferMs + " ms")}, " +
            $"capture cadence {_worstCaptureCadenceMs:0.##} ms, render period {_worstRenderPeriodMs:0.##} ms, " +
            $"render buffer {_minRealRenderBufferMs} ms (requested {b.RenderBufferRequestMs}), padding {b.RenderPaddingMs} ms, " +
            $"ring target {b.RingTargetMs} ms (floor {b.RingFloorMs}, headroom {_autoHeadroomMs}, safety {_safetyPeriods}), " +
            $"effective total {b.EffectiveTotalMs} ms (floor {b.FloorTotalMs}), exclusive {(_exclusiveRenderActive ? "ON" : "off")}; " +
            string.Join(", ", started.Select(d => $"'{d.Info.Name}' {d.Render?.Tier} buf {d.RealRenderBufferMs} ms period {d.RenderPeriodMs} ms")) +
            "; inputs " + string.Join(", ", _inputDevices.Where(d => d.Capture != null)
                .Select(d => $"'{d.Info.Name}' {d.Capture?.Tier} cadence {d.EffectiveCadenceMs:0.##} ms ring {d.FillTargetFrames * 1000 / Math.Max(1, _engineSampleRate)} ms")));
    }

    private (int BaseMasterTargetFrames, int MaxMasterTargetFrames) CalculateSyncTargetFrames()
    {
        int sampleRate = _engineSampleRate > 0 ? _engineSampleRate : 48000;

        int desiredByOutputBufferFrames = Math.Max(sampleRate * _outputBufferMs / 1000, sampleRate / 200);
        int ringCapacityFrames = sampleRate * RingBufferCapacityMs / 1000;
        int maxSafeTargetFrames = Math.Max(64, (ringCapacityFrames * 4) / 5);
        int baseTargetFrames = Math.Max(64, Math.Min(desiredByOutputBufferFrames, maxSafeTargetFrames));

        return (baseTargetFrames, Math.Max(baseTargetFrames, maxSafeTargetFrames));
    }

    public void RefreshDevices()
    {
        // Remove devices that no longer exist
        var captureDevices = _enumerator.GetDevices(DataFlow.Capture);
        var renderDevices = _enumerator.GetDevices(DataFlow.Render);

        static bool IsInputStillAvailable(ActiveDevice input, List<DeviceInfo> captures, List<DeviceInfo> renders)
        {
            if (input.IsLoopback || input.Info.Id.StartsWith("loop:", StringComparison.Ordinal))
            {
                var renderId = input.Info.Id.StartsWith("loop:", StringComparison.Ordinal)
                    ? input.Info.Id.Substring("loop:".Length)
                    : input.Info.Id;
                return renders.Any(r => r.Id == renderId);
            }

            return captures.Any(c => c.Id == input.Info.Id);
        }

        // Identify inputs and outputs that will be removed
        var inputsToRemove = _inputDevices.Where(d => !IsInputStillAvailable(d, captureDevices, renderDevices)).ToList();
        var outputsToRemove = _outputDevices.Where(d => !renderDevices.Any(rd => rd.Id == d.Info.Id)).ToList();

        // Identify devices that have reappeared and have dormant routes waiting on them.
        // This is the path that brings monitor/HDMI tiles back when displays wake up.
        var activeInputIds = new HashSet<string>(_inputDevices.Select(d => d.Info.Id), StringComparer.Ordinal);
        var activeOutputIds = new HashSet<string>(_outputDevices.Select(d => d.Info.Id), StringComparer.Ordinal);

        bool InputAvailable(string id)
        {
            if (id.StartsWith("loop:", StringComparison.Ordinal))
            {
                var renderId = id.Substring("loop:".Length);
                return renderDevices.Any(r => r.Id == renderId);
            }
            return captureDevices.Any(c => c.Id == id);
        }

        var inputsToReattach = _dormantRoutes
            .Select(r => r.InputDeviceId)
            .Where(id => !activeInputIds.Contains(id) && InputAvailable(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var outputsToReattach = _dormantRoutes
            .Select(r => r.OutputDeviceId)
            .Where(id => !activeOutputIds.Contains(id) && renderDevices.Any(d => d.Id == id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // Format drift on devices that are still present (HDMI renegotiation, Sound
        // settings edit): adopt the new rate/channel count — the rest of this method
        // rebuilds offsets, matrix and (if running) the engine around it.
        bool formatChanged = false;
        foreach (var dev in _inputDevices)
        {
            var fresh = dev.Info.Id.StartsWith("loop:", StringComparison.Ordinal)
                ? renderDevices.FirstOrDefault(r => r.Id == dev.Info.Id.Substring("loop:".Length))
                : captureDevices.FirstOrDefault(c => c.Id == dev.Info.Id);
            if (fresh is null) continue;
            if (fresh.SampleRate != dev.Info.SampleRate || fresh.Channels != dev.Info.Channels)
            {
                Wasapi.WasapiDiagnostics.Log($"[Engine] input format changed on '{dev.Info.Name}': " +
                    $"{dev.Info.SampleRate}Hz/{dev.Info.Channels}ch → {fresh.SampleRate}Hz/{fresh.Channels}ch");
                dev.Info = dev.Info with { SampleRate = fresh.SampleRate, Channels = fresh.Channels };
                formatChanged = true;
            }
        }
        foreach (var dev in _outputDevices)
        {
            var fresh = renderDevices.FirstOrDefault(r => r.Id == dev.Info.Id);
            if (fresh is null) continue;
            if (fresh.SampleRate != dev.Info.SampleRate || fresh.Channels != dev.Info.Channels)
            {
                Wasapi.WasapiDiagnostics.Log($"[Engine] output format changed on '{dev.Info.Name}': " +
                    $"{dev.Info.SampleRate}Hz/{dev.Info.Channels}ch → {fresh.SampleRate}Hz/{fresh.Channels}ch");
                dev.Info = dev.Info with { SampleRate = fresh.SampleRate, Channels = fresh.Channels };
                formatChanged = true;
            }
        }

        bool changed = inputsToRemove.Count > 0 || outputsToRemove.Count > 0
            || inputsToReattach.Count > 0 || outputsToReattach.Count > 0 || formatChanged;

        if (!changed)
        {
            return;
        }

        Wasapi.WasapiDiagnostics.Log($"[Engine] device refresh: -{inputsToRemove.Count} in, -{outputsToRemove.Count} out, " +
            $"+{inputsToReattach.Count} in, +{outputsToReattach.Count} out, formatChanged {formatChanged}");

        // Capture routes for devices that are being removed, before removing them
        CaptureRoutesForRemovedDevices(inputsToRemove, outputsToRemove);

        var routeSnapshot = CaptureRoutedCrosspoints();
        bool wasRunning = _running;
        if (wasRunning)
        {
            Stop();
        }

        for (int i = _inputDevices.Count - 1; i >= 0; i--)
        {
            if (!IsInputStillAvailable(_inputDevices[i], captureDevices, renderDevices))
            {
                _inputDevices.RemoveAt(i);
            }
        }
        for (int i = _outputDevices.Count - 1; i >= 0; i--)
        {
            if (!renderDevices.Any(d => d.Id == _outputDevices[i].Info.Id))
            {
                _outputDevices.RemoveAt(i);
            }
        }

        RecalcChannelOffsets();
        RestoreRoutedCrosspoints(routeSnapshot);

        // Reattach devices that came back online (e.g. monitor woke up). AddInput/Output
        // will run RestoreDormantRoutesForXxxDevice and re-establish their routes against
        // any peer that's also currently active.
        foreach (var id in inputsToReattach)
        {
            AddInputDevice(id);
        }
        foreach (var id in outputsToReattach)
        {
            AddOutputDevice(id);
        }

        if (wasRunning && _inputDevices.Count > 0 && _outputDevices.Count > 0 && _routingMatrix.HasAnyCrosspoints())
        {
            Start();
        }

        StateChanged?.Invoke();
    }

    private void CaptureRoutesForRemovedDevices(List<ActiveDevice> inputsToRemove, List<ActiveDevice> outputsToRemove)
    {
        var removedInputIds = new HashSet<string>(inputsToRemove.Select(d => d.Info.Id));
        var removedOutputIds = new HashSet<string>(outputsToRemove.Select(d => d.Info.Id));

        if (removedInputIds.Count == 0 && removedOutputIds.Count == 0)
            return;

        var front = _routingMatrix.GetFrontBuffer();
        if (front.Length == 0 || _routingMatrix.OutputChannels == 0)
            return;

        int outChannels = _routingMatrix.OutputChannels;
        for (int inCh = 0; inCh < _routingMatrix.InputChannels; inCh++)
        {
            for (int outCh = 0; outCh < outChannels; outCh++)
            {
                int idx = inCh * outChannels + outCh;
                if (idx < 0 || idx >= front.Length) continue;

                var cp = front[idx];
                if (!cp.Active) continue;

                var inDevice = FindInputDeviceByChannel(inCh);
                var outDevice = FindOutputDeviceByChannel(outCh);
                if (inDevice == null || outDevice == null) continue;

                // Only capture if at least one device is being removed
                if (!removedInputIds.Contains(inDevice.Info.Id) && !removedOutputIds.Contains(outDevice.Info.Id))
                    continue;

                int inLocal = inCh - inDevice.GlobalChannelOffset;
                int outLocal = outCh - outDevice.GlobalChannelOffset;
                if (inLocal < 0 || outLocal < 0) continue;

                float gainDb = cp.Gain <= 0f ? -60f : 20f * MathF.Log10(cp.Gain);

                // Add or update this route in dormant routes
                var route = new RoutedCrosspoint(
                    inDevice.Info.Id,
                    inLocal,
                    outDevice.Info.Id,
                    outLocal,
                    cp.Active,
                    gainDb,
                    cp.PhaseInverted);

                int existingIndex = _dormantRoutes.FindIndex(r =>
                    r.InputDeviceId == inDevice.Info.Id &&
                    r.InputLocalChannel == inLocal &&
                    r.OutputDeviceId == outDevice.Info.Id &&
                    r.OutputLocalChannel == outLocal);

                if (existingIndex < 0)
                {
                    _dormantRoutes.Add(route);
                }
                else
                {
                    _dormantRoutes[existingIndex] = route;
                }
            }
        }
    }

    private List<RoutedCrosspoint> CaptureRoutedCrosspoints()
    {
        var snapshot = new List<RoutedCrosspoint>();
        var front = _routingMatrix.GetFrontBuffer();
        if (front.Length == 0 || _routingMatrix.OutputChannels == 0)
        {
            return snapshot;
        }

        int outChannels = _routingMatrix.OutputChannels;
        for (int inCh = 0; inCh < _routingMatrix.InputChannels; inCh++)
        {
            for (int outCh = 0; outCh < outChannels; outCh++)
            {
                int idx = inCh * outChannels + outCh;
                if (idx < 0 || idx >= front.Length) continue;

                var cp = front[idx];
                if (!cp.Active) continue;

                var inDevice = FindInputDeviceByChannel(inCh);
                var outDevice = FindOutputDeviceByChannel(outCh);
                if (inDevice == null || outDevice == null) continue;

                int inLocal = inCh - inDevice.GlobalChannelOffset;
                int outLocal = outCh - outDevice.GlobalChannelOffset;
                if (inLocal < 0 || outLocal < 0) continue;

                float gainDb = cp.Gain <= 0f ? -60f : 20f * MathF.Log10(cp.Gain);
                snapshot.Add(new RoutedCrosspoint(
                    inDevice.Info.Id,
                    inLocal,
                    outDevice.Info.Id,
                    outLocal,
                    cp.Active,
                    gainDb,
                    cp.PhaseInverted));
            }
        }

        return snapshot;
    }

    private void RestoreRoutedCrosspoints(IEnumerable<RoutedCrosspoint> snapshot)
    {
        _routingMatrix.ClearAll();

        var updates = new List<(int InCh, int OutCh, bool Active, float GainDb, bool PhaseInverted)>();
        foreach (var route in snapshot)
        {
            var inDevice = _inputDevices.FirstOrDefault(d => d.Info.Id == route.InputDeviceId);
            var outDevice = _outputDevices.FirstOrDefault(d => d.Info.Id == route.OutputDeviceId);
            if (inDevice == null || outDevice == null) continue;

            if (route.InputLocalChannel < 0 || route.InputLocalChannel >= inDevice.Info.Channels) continue;
            if (route.OutputLocalChannel < 0 || route.OutputLocalChannel >= outDevice.Info.Channels) continue;

            int inGlobal = inDevice.GlobalChannelOffset + route.InputLocalChannel;
            int outGlobal = outDevice.GlobalChannelOffset + route.OutputLocalChannel;
            updates.Add((inGlobal, outGlobal, route.Active, route.GainDb, route.PhaseInverted));
        }

        _routingMatrix.SetCrosspoints(updates);
    }

    public void Dispose()
    {
        _healthTimer?.Dispose();
        _healthTimer = null;
        Stop();
        _enumerator.Dispose();
    }
}

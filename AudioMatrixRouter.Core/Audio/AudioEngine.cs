using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Threading;

namespace AudioMatrixRouter.Audio;

public class ActiveDevice
{
    public required DeviceInfo Info { get; init; }
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

    private Timer? _degradeTimer;
    private int _degradeBusy;

    private void ArmDegradeTimer()
    {
        _degradeTimer?.Dispose();
        _degradeTimer = new Timer(_ => EvaluateAdaptiveDegrade(), null, 1000, 1000);
    }

    private void EvaluateAdaptiveDegrade()
    {
        if (!_running) return;
        if (Interlocked.CompareExchange(ref _degradeBusy, 1, 0) != 0) return;
        try
        {
            const double WindowSec = 10;
            const long Threshold = 5;
            bool anyEvaluating = false;
            bool demoted = false;
            double freq = System.Diagnostics.Stopwatch.Frequency;

            foreach (var dev in _outputDevices)
            {
                if (dev.Render is null) continue;
                if (dev.Render.Tier is not (EndpointTier.MinPeriod or EndpointTier.DoublePeriod)) continue;
                double elapsed = (System.Diagnostics.Stopwatch.GetTimestamp() - dev.TierStartTimestamp) / freq;
                if (elapsed > WindowSec) continue;
                anyEvaluating = true;

                long delta = (dev.MixProvider?.UnderrunCount ?? 0) - dev.TierUnderrunBaseline;
                if (delta > Threshold && dev.TierDegradeCount < 2)
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
                if (cap.Tier is not (EndpointTier.MinPeriod or EndpointTier.DoublePeriod)) continue;
                anyEvaluating = true;

                long delta = cap.DiscontinuityCount + Interlocked.Read(ref dev.InputOverflowCount)
                             - dev.TierDiscontinuityBaseline;
                if (delta > Threshold && dev.TierDegradeCount < 2)
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
                // Timer thread, never an audio thread — a direct restart is safe here
                // and goes through the proven barrier/fill path.
                FullRestart();
            }
            else if (!anyEvaluating)
            {
                _degradeTimer?.Dispose();
                _degradeTimer = null; // self-disarm: zero steady-state cost
            }
        }
        catch
        {
            // Degrade evaluation must never take the engine down.
        }
        finally
        {
            Interlocked.Exchange(ref _degradeBusy, 0);
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
    }

    public bool SetInputMasterDevice(string deviceId)
    {
        // The input-master flag is retained for UI/telemetry; the sync architecture
        // disciplines every input independently against the engine clock, so it has
        // no controller role anymore.
        if (string.IsNullOrWhiteSpace(deviceId))
        {
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

        var device = _inputDevices.FirstOrDefault(d => d.Info.Id == deviceId);
        if (device == null) return false;

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
            StateChanged?.Invoke();
            return true;
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

    public ActiveDevice? GetInputMasterDevice() =>
        _inputDevices.FirstOrDefault(d => d.IsMasterDevice) ??
        _inputDevices.FirstOrDefault();

    public ActiveDevice? GetOutputMasterDevice() =>
        _outputDevices.FirstOrDefault(d => d.IsMasterDevice) ??
        _outputDevices.FirstOrDefault();

    /// <summary>
    /// Master to try first on a fresh Start(): the user's durable preference wins whenever
    /// that device is currently attached; otherwise fall back to the session flags.
    /// </summary>
    private ActiveDevice? ResolveStartMaster()
    {
        if (!string.IsNullOrWhiteSpace(_preferredOutputMasterId))
        {
            var preferred = _outputDevices.FirstOrDefault(d => d.Info.Id == _preferredOutputMasterId);
            if (preferred != null) return preferred;
        }
        return GetOutputMasterDevice();
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

        bool changed = _routingMatrix.SetCrosspoint(inCh, outCh, active, gainDb, phaseInverted);
        if (!changed)
        {
            return;
        }

        _routingMatrix.Publish();
        StateChanged?.Invoke();
    }

    public (int Changed, int Skipped) SetCrosspoints(IEnumerable<(int InCh, int OutCh, bool Active, float GainDb, bool PhaseInverted)> updates)
    {
        var list = updates as IReadOnlyCollection<(int InCh, int OutCh, bool Active, float GainDb, bool PhaseInverted)> ?? updates.ToList();
        foreach (var update in list)
        {
            if (!update.Active) PruneDormantRoute(update.InCh, update.OutCh);
        }

        var result = _routingMatrix.SetCrosspoints(list);
        if (result.Changed > 0)
        {
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

    private bool StartCore(ActiveDevice? masterOverride, int attempt)
    {
        if (_running) return true;
        if (_inputDevices.Count == 0 || _outputDevices.Count == 0) return false;

        try
        {
            var masterOutput = masterOverride ?? ResolveStartMaster() ?? _outputDevices.First();

            // ===== 1. Engine clock = master output's nominal rate =====
            _engineSampleRate = masterOutput.Info.SampleRate > 0 ? masterOutput.Info.SampleRate : 48000;

            // Diagnostic: log each endpoint's real shared-mode period capabilities
            // (%TEMP%\amr-wasapi-probe.log) — the numbers the small-period path uses.
            try
            {
                Wasapi.WasapiDiagnostics.ProbeEnginePeriods("out " + masterOutput.Info.Name, masterOutput.Info.Id);
                var probeMic = _inputDevices.FirstOrDefault(d => !d.IsLoopback);
                if (probeMic != null)
                    Wasapi.WasapiDiagnostics.ProbeEnginePeriods("in " + probeMic.Info.Name, probeMic.Info.Id);
            }
            catch { }

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
            _worstRenderGulpMs = 0; // real render numbers arrive in step 4
            var (baseMasterTargetFrames, maxMasterTargetFrames) = CalculateSyncTargetFrames();
            _syncCoordinator = new OutputSyncCoordinator(
                masterOutput.Info.Id, _engineSampleRate, baseMasterTargetFrames, maxMasterTargetFrames);
            _syncCoordinator.ArmGlobalRefillHold();

            var sources = _inputDevices
                .Where(d => d.RingBuffer != null)
                .Select(d => new MixingSampleProvider.CaptureSource(
                    d.Info.Id,
                    d.RingBuffer!,
                    d.GlobalChannelOffset,
                    d.Info.Channels,
                    d.IsMasterDevice))
                .ToList();

            // ===== 4. Providers + renders (consumer cursors pinned on EMPTY rings) =====
            var startedOutputs = new List<ActiveDevice>();
            foreach (var dev in _outputDevices)
            {
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

                if (!TryInitRender(dev, mmDevice, RenderBufferMs))
                {
                    try { dev.MixProvider?.DetachConsumer(); } catch { }
                    dev.MixProvider = null;
                    dev.ConsumerId = string.Empty;
                    continue; // Skip this device; engine continues with the remaining outputs.
                }

                startedOutputs.Add(dev);
            }

            // Startup is not valid without at least one active render endpoint.
            if (startedOutputs.Count == 0)
            {
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
                    return StartCore(runtimeMaster, attempt + 1);
                }

                _syncCoordinator.SetMasterConsumer(runtimeMaster.Info.Id);
                _syncCoordinator.ArmGlobalRefillHold();
                masterOutput = runtimeMaster;
            }

            // Preferred consumer for ring trim ordering + the input ASRC fill reference.
            ApplyPreferredMasterConsumerToInputs();

            // Real render buffers are known now — pin the fill floor to the worst REAL
            // gulp and make the barrier release at EXACTLY the servo's fill target.
            // (The old path reconstructed this as knob+headroom and clamped the
            // headroom at 0, so any knob > ~40 released above target and hard-drained
            // an audible skip right after every start.)
            _worstRenderGulpMs = startedOutputs.Max(d =>
                d.RealRenderBufferMs > 0 ? d.RealRenderBufferMs : RenderBufferMs);
            _syncCoordinator.SetReleaseFillTargetMs(InputFillTargetMs);

            // ===== 5. Start captures (rings begin filling; cursors already pinned) =====
            _worstCaptureCadenceMs = 10; // provisional until real cadences are known
            int inputFillTargetFrames = CalculateInputFillTargetFrames();
            foreach (var dev in _inputDevices)
            {
                if (dev.RingBuffer == null) continue;
                if (!CreateAndStartCapture(dev, inputFillTargetFrames, masterOutput.Info.Id))
                {
                    continue;
                }
            }

            // Real cadences are known now (small-period mics can be < 10 ms):
            // recompute the fill target and retarget every servo + the barrier
            // BEFORE release — the rings are still pre-filling, so this is glitch-free.
            _worstCaptureCadenceMs = _inputDevices
                .Where(d => d.Capture != null)
                .Select(d => d.CaptureCadenceMs)
                .DefaultIfEmpty(10)
                .Max();
            int refinedFillFrames = CalculateInputFillTargetFrames();
            foreach (var dev in _inputDevices)
            {
                dev.InputAsrc?.SetTargetFillFrames(refinedFillFrames);
            }
            _syncCoordinator.SetReleaseFillTargetMs(InputFillTargetMs);

            // ===== 6. Play all outputs together =====
            foreach (var dev in _outputDevices)
            {
                try { dev.Render?.Play(); } catch { }
            }

            _running = true;
            ArmDegradeTimer();
            StateChanged?.Invoke();
            return true;
        }
        catch
        {
            Stop();
            return false;
        }
    }

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

    public bool SetInputBufferMs(int bufferMs)
    {
        int clamped = Math.Clamp(bufferMs, 10, 200);
        if (_inputBufferMs == clamped)
        {
            return true;
        }

        _inputBufferMs = clamped;

        foreach (var dev in _inputDevices)
        {
            dev.CaptureLatencyMs = clamped;
        }

        if (_running)
        {
            FullRestart();
        }

        StateChanged?.Invoke();
        return true;
    }

    private bool TryInitRender(ActiveDevice dev, MMDevice mmDevice, int latencyMs)
    {
        // "auto" backend: our IAudioClient3 client (small-period ladder inside it).
        // A total failure of the custom client falls through to NAudio — the belt
        // under the ladder's own braces.
        if (!string.Equals(_backend, "legacy", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                IRenderEndpoint rc = new Wasapi.WasapiRenderClient(
                    dev.Info.Id, latencyMs, dev.RequestedRenderTier, dev.Info.Name);
                rc.Init(dev.MixProvider!);
                AdoptRender(dev, rc, latencyMs);
                return true;
            }
            catch
            {
                try { dev.Render?.Dispose(); } catch { }
                dev.Render = null;
            }
        }

        try
        {
            IRenderEndpoint render = new NAudioRenderEndpoint(mmDevice, latencyMs, dev.Info.SampleRate);
            render.Init(dev.MixProvider!);
            AdoptRender(dev, render, latencyMs);
            return true;
        }
        catch
        {
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
        dev.TierStartTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        // Honest numbers come from the endpoint (real buffer + one period of
        // mix-ahead); fall back to the request when read-back was unavailable.
        dev.RealRenderBufferMs = render.ActualBufferMs;
        dev.RenderPeriodMs = (int)Math.Ceiling(render.ActualPeriodMs);
        dev.RenderLatencyMs = render.ActualBufferMs > 0
            ? render.ActualBufferMs + dev.RenderPeriodMs
            : requestedMs;
    }

    private bool CreateAndStartCapture(ActiveDevice dev, int fillTargetFrames, string masterConsumerId)
    {
        if (dev.RingBuffer == null)
        {
            return false;
        }

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
        // Loopbacks use the polled 20 ms / ~10 ms-cadence mode.
        string endpointId = dev.IsLoopback && dev.Info.Id.StartsWith("loop:", StringComparison.Ordinal)
            ? dev.Info.Id.Substring("loop:".Length)
            : dev.Info.Id;
        ICaptureEndpoint? custom = null;
        if (!string.Equals(_backend, "legacy", StringComparison.OrdinalIgnoreCase))
        {
            custom = new Wasapi.WasapiCaptureClient(
                endpointId,
                dev.IsLoopback
                    ? Wasapi.WasapiCaptureClient.CaptureMode.PolledLoopback
                    : Wasapi.WasapiCaptureClient.CaptureMode.EventMic,
                dev.RequestedCaptureTier,
                dev.Info.Name);
        }
        dev.Capture = custom ?? new NAudioCaptureEndpoint(mmDevice, dev.IsLoopback,
            Math.Max(40, _inputBufferMs), dev.Info.SampleRate, dev.Info.Channels);

        // Capture-side ASRC: convert this device's stream into the engine clock domain.
        // Handles BOTH static rate mismatch (e.g. 44.1k capture → 48k engine) and crystal
        // drift (fill-level PI), so the rings always contain coherent engine-rate audio.
        var asrc = new InputAsrc(dev.RingBuffer, dev.Info.Channels, dev.Info.SampleRate, _engineSampleRate, fillTargetFrames);
        asrc.SetFillConsumer(masterConsumerId);
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

        // Latency contribution = delivery cadence (one period), NOT buffer depth.
        dev.CaptureCadenceMs = dev.Capture.ActualPeriodMs;
        dev.CaptureLatencyMs = (int)Math.Ceiling(dev.CaptureCadenceMs);
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

    public bool SetOutputBufferMs(int bufferMs)
    {
        int clamped = Math.Clamp(bufferMs, 10, 200);
        if (_outputBufferMs == clamped)
        {
            return true;
        }

        _outputBufferMs = clamped;
        foreach (var dev in _outputDevices)
        {
            dev.MixProvider?.SetOutputBufferMs(clamped);
        }

        if (_running)
        {
            FullRestart();
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
        _degradeTimer?.Dispose();
        _degradeTimer = null;

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
        var masterOutput = GetOutputMasterDevice() ?? _outputDevices.FirstOrDefault();
        if (masterOutput == null) return;

        var preferredConsumerId = string.IsNullOrWhiteSpace(masterOutput.ConsumerId)
            ? masterOutput.Info.Id
            : masterOutput.ConsumerId;

        foreach (var input in _inputDevices)
        {
            input.RingBuffer?.SetPreferredConsumer(preferredConsumerId);
            input.InputAsrc?.SetFillConsumer(preferredConsumerId);
        }
    }

    /// <summary>Render buffer share of the latency budget (see the budget comment).</summary>
    private int RenderBufferMs => Math.Clamp(_outputBufferMs / 4, 10, 50);

    // Worst REAL render gulp across started outputs (WASAPI tops the buffer to full
    // each period, so the gulp = the real buffer size). 0 until renders initialize.
    private int _worstRenderGulpMs;

    // Worst REAL capture delivery cadence across started inputs (2.67-10ms for
    // small-period mics, 10 for default periods and polled loopbacks).
    private double _worstCaptureCadenceMs = 10;

    /// <summary>Ring fill target: the budget minus capture cadence and render, floored
    /// so one REAL render gulp + one capture block + safety can never starve the ring.</summary>
    private int InputFillTargetMs
    {
        get
        {
            int gulp = _worstRenderGulpMs > 0 ? _worstRenderGulpMs : RenderBufferMs;
            int cadence = (int)Math.Ceiling(_worstCaptureCadenceMs > 0 ? _worstCaptureCadenceMs : 10);
            return Math.Clamp(
                _outputBufferMs - cadence - RenderBufferMs,
                gulp + cadence + 5,
                RingBufferCapacityMs * 3 / 4);
        }
    }

    private int CalculateInputFillTargetFrames() =>
        Math.Max(64, _engineSampleRate * InputFillTargetMs / 1000);

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

        bool changed = inputsToRemove.Count > 0 || outputsToRemove.Count > 0
            || inputsToReattach.Count > 0 || outputsToReattach.Count > 0;

        if (!changed)
        {
            return;
        }

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
        Stop();
        _enumerator.Dispose();
    }
}

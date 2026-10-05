using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Threading;

namespace AudioMatrixRouter.Audio;

public class ActiveDevice
{
    public required DeviceInfo Info { get; init; }
    public int GlobalChannelOffset { get; set; }
    public RingBuffer? RingBuffer { get; set; }

    // Standard transport (NAudio shared-mode WASAPI, default engine period).
    public WasapiCapture? Capture { get; set; }
    public WasapiOut? Render { get; set; }
    public MMDevice? StandardRenderEndpoint { get; set; }

    // High-performance transport (own WASAPI layer, minimum shared period, MMCSS threads).
    public LowLatencyCaptureClient? HpCapture { get; set; }
    public LowLatencyRenderClient? HpRender { get; set; }

    public MixingSampleProvider? MixProvider { get; set; }
    public InputAsrc? InputAsrc { get; set; }
    public bool IsMasterDevice { get; set; }
    public int OutputDelayMs { get; set; }
    public string ConsumerId { get; set; } = string.Empty;
    public long InputOverflowCount;

    /// <summary>Capture callback period (ms). This IS the input-side driver latency: audio
    /// reaches the engine one period after it is captured, regardless of buffer length.</summary>
    public double CapturePeriodMs { get; set; }
    /// <summary>Render callback period (ms): the engine's own mixing granularity on this endpoint.</summary>
    public double RenderPeriodMs { get; set; }
    /// <summary>Audio held ahead of the render engine's read cursor (ms).</summary>
    public double RenderDepthMs { get; set; }
    /// <summary>Ring fill the input ASRC servo holds for this input (engine frames).</summary>
    public int FillTargetFrames { get; set; }
    /// <summary>True when a sub-default (IAudioClient3) engine period was negotiated.</summary>
    public bool LowLatencyPeriod { get; set; }
    public string TransportLabel { get; set; } = string.Empty;

    public bool IsLoopback { get; set; }
    public double BaseLatencyMs { get; set; }
    // Per-channel running peak (0..1). Producer writes; UI samples and resets atomically.
    public float[]? PeakLevels;

    /// <summary>Input-path driver latency (ms): one capture period.</summary>
    public double CaptureLatencyMs => CapturePeriodMs;
    /// <summary>Output-path driver latency (ms): held depth plus one engine period.</summary>
    public double RenderLatencyMs => RenderDepthMs + RenderPeriodMs;
}

/// <summary>
/// AudioEngine — sync-architecture with period-derived buffering.
///
/// Clocking model:
///   * ENGINE CLOCK = master output device's nominal sample rate / render callback.
///   * Capture side: each input's WASAPI callback feeds an InputAsrc that converts the
///     capture stream (any rate, any crystal) into engine-rate frames before they enter
///     the shared ring. A fill-level PI per input absorbs capture-crystal drift.
///   * Render side: each output's MixingSampleProvider consumes engine-rate frames and
///     resamples to its device rate; followers carry a ppm trim from the coordinator's
///     cursor-truth phase loop. The master output plays at exactly 1.0.
///
/// Buffering model (one knob):
///   The user sets a single SAFETY MARGIN in ms. Every buffer is derived from the real
///   device periods plus that margin:
///     render depth      = 2 × render period + margin
///     ring fill target  = capture period + master render period + margin   (per input)
///   The start barrier releases at exactly the ring target, so steady-state latency is
///   reached immediately instead of the servo crawling to it after start.
///
/// Transports:
///   * Standard: NAudio shared-mode WASAPI at the OS default period (~10 ms). Loopback
///     inputs are polled by NAudio every ~50 ms, which the fill target accounts for.
///   * High performance: own WASAPI layer — IAudioClient3 minimum shared period,
///     event-driven loopback with keep-alive, MMCSS "Pro Audio" threads, exact-depth render.
///
/// Startup ordering (matters for phase zero):
///   transports prepared (periods known) → rings → fill targets → coordinator → providers
///   (consumer cursors pinned on empty rings) → renders bound → captures started →
///   renders played together.
/// </summary>
public class AudioEngine : IDisposable
{
    // Ring capacity stays at a stable ceiling; the margin moves targets, not allocations.
    private const int RingBufferCapacityMs = 400;
    // Standard-transport capture buffer: overflow headroom only, never a latency term.
    private const int StandardCaptureBufferMs = 100;
    // NAudio's loopback capture polls at half of its fixed 100 ms buffer.
    private const double StandardLoopbackPollMs = 50;
    private const double FallbackPeriodMs = 10;

    public const int MarginMinMs = 0;
    public const int MarginMaxMs = 100;
    public const int DefaultStandardMarginMs = 20;
    public const int DefaultHighPerfMarginMs = 4;

    private readonly DeviceEnumerator _enumerator = new();
    private readonly List<ActiveDevice> _inputDevices = [];
    private readonly List<ActiveDevice> _outputDevices = [];
    private readonly RoutingMatrix _routingMatrix = new();
    private bool _running;
    private OutputSyncCoordinator? _syncCoordinator;
    private int _standardMarginMs = DefaultStandardMarginMs;
    private int _highPerfMarginMs = DefaultHighPerfMarginMs;
    private bool _highPerformanceMode;
    private int _engineSampleRate = 48000;

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
        float GainDb);

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

    /// <summary>Safety margin (ms) of the ACTIVE mode.</summary>
    public int BufferMarginMs => _highPerformanceMode ? _highPerfMarginMs : _standardMarginMs;
    public int StandardBufferMarginMs => _standardMarginMs;
    public int HighPerfBufferMarginMs => _highPerfMarginMs;
    public bool HighPerformanceMode => _highPerformanceMode;

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

        latencyMs = input.CaptureLatencyMs + captureQueueMs + output.RenderLatencyMs + output.OutputDelayMs;
        return true;
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

        double queueMs = 0;
        if (_engineSampleRate > 0)
        {
            int queuedFrames = inputMaster.RingBuffer.GetAvailableFrames(consumerId);
            queueMs = (queuedFrames * 1000.0) / _engineSampleRate;
        }

        latencyMs = inputMaster.CaptureLatencyMs + queueMs;
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

        latencyMs = outputMaster.RenderLatencyMs + outputMaster.OutputDelayMs;
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

        var device = _outputDevices.FirstOrDefault(d => d.Info.Id == deviceId);
        if (device == null) return false;

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

    public ActiveDevice? GetInputMasterDevice() =>
        _inputDevices.FirstOrDefault(d => d.IsMasterDevice) ??
        _inputDevices.FirstOrDefault();

    public ActiveDevice? GetOutputMasterDevice() =>
        _outputDevices.FirstOrDefault(d => d.IsMasterDevice) ??
        _outputDevices.FirstOrDefault();

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
        _inputDevices.Add(ad);
        RecalcChannelOffsets();

        RestoreDormantRoutesForInputDevice(deviceId);

        if (wasRunning && _inputDevices.Count > 0 && _outputDevices.Count > 0 && _routingMatrix.HasAnyCrosspoints()) Start();

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
        _outputDevices.Add(ad);
        RecalcChannelOffsets();

        RestoreDormantRoutesForOutputDevice(deviceId);

        if (wasRunning && _inputDevices.Count > 0 && _outputDevices.Count > 0 && _routingMatrix.HasAnyCrosspoints()) Start();

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
        foreach (var dormant in _dormantRoutes.Where(r => r.InputDeviceId == inputDeviceId).ToList())
        {
            var outDev = _outputDevices.FirstOrDefault(d => d.Info.Id == dormant.OutputDeviceId);
            if (outDev == null) continue;
            if (dormant.InputLocalChannel < 0 || dormant.InputLocalChannel >= inDev.Info.Channels) continue;
            if (dormant.OutputLocalChannel < 0 || dormant.OutputLocalChannel >= outDev.Info.Channels) continue;

            int inGlobal = inDev.GlobalChannelOffset + dormant.InputLocalChannel;
            int outGlobal = outDev.GlobalChannelOffset + dormant.OutputLocalChannel;
            _routingMatrix.SetCrosspoint(inGlobal, outGlobal, dormant.Active, dormant.GainDb);
            restored.Add(dormant);
        }

        if (restored.Count > 0)
        {
            foreach (var r in restored) _dormantRoutes.Remove(r);
            _routingMatrix.Publish();
        }
    }

    private void RestoreDormantRoutesForOutputDevice(string outputDeviceId)
    {
        var outDev = _outputDevices.FirstOrDefault(d => d.Info.Id == outputDeviceId);
        if (outDev == null) return;

        var restored = new List<RoutedCrosspoint>();
        foreach (var dormant in _dormantRoutes.Where(r => r.OutputDeviceId == outputDeviceId).ToList())
        {
            var inDev = _inputDevices.FirstOrDefault(d => d.Info.Id == dormant.InputDeviceId);
            if (inDev == null) continue;
            if (dormant.InputLocalChannel < 0 || dormant.InputLocalChannel >= inDev.Info.Channels) continue;
            if (dormant.OutputLocalChannel < 0 || dormant.OutputLocalChannel >= outDev.Info.Channels) continue;

            int inGlobal = inDev.GlobalChannelOffset + dormant.InputLocalChannel;
            int outGlobal = outDev.GlobalChannelOffset + dormant.OutputLocalChannel;
            _routingMatrix.SetCrosspoint(inGlobal, outGlobal, dormant.Active, dormant.GainDb);
            restored.Add(dormant);
        }

        if (restored.Count > 0)
        {
            foreach (var r in restored) _dormantRoutes.Remove(r);
            _routingMatrix.Publish();
        }
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
        _inputDevices.RemoveAt(index);
        RecalcChannelOffsets();
        RestoreRoutedCrosspoints(routeSnapshot);
        if (wasRunning && _inputDevices.Count > 0 && _outputDevices.Count > 0 && _routingMatrix.HasAnyCrosspoints()) Start();
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
        _outputDevices.RemoveAt(index);
        RecalcChannelOffsets();
        RestoreRoutedCrosspoints(routeSnapshot);
        if (wasRunning && _inputDevices.Count > 0 && _outputDevices.Count > 0 && _routingMatrix.HasAnyCrosspoints()) Start();
        StateChanged?.Invoke();
    }

    public void SetCrosspoint(int inCh, int outCh, bool active, float gainDb, bool phaseInverted = false)
    {
        bool changed = _routingMatrix.SetCrosspoint(inCh, outCh, active, gainDb, phaseInverted);
        if (!changed)
        {
            return;
        }

        _routingMatrix.Publish();
        StateChanged?.Invoke();
    }

    public int SetCrosspoints(IEnumerable<(int InCh, int OutCh, bool Active, float GainDb, bool PhaseInverted)> updates)
    {
        int changed = _routingMatrix.SetCrosspoints(updates);
        if (changed > 0)
        {
            StateChanged?.Invoke();
        }

        return changed;
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

    public void ToggleCrosspoint(int inCh, int outCh)
    {
        _routingMatrix.ToggleCrosspoint(inCh, outCh);
        _routingMatrix.Publish();
        StateChanged?.Invoke();
    }

    public void ClearCrosspoints()
    {
        _routingMatrix.ClearAll();
        StateChanged?.Invoke();
    }

    public bool Start()
    {
        if (_running) return true;
        if (_inputDevices.Count == 0 || _outputDevices.Count == 0) return false;

        try
        {
            var masterOutput = GetOutputMasterDevice() ?? _outputDevices.First();
            int marginMs = BufferMarginMs;

            // ===== 1. Engine clock = master output's nominal rate =====
            _engineSampleRate = masterOutput.Info.SampleRate > 0 ? masterOutput.Info.SampleRate : 48000;

            // ===== 2. Rings + capture transports prepared (periods become known) =====
            int ringFrames = Math.Max(_engineSampleRate * RingBufferCapacityMs / 1000, _engineSampleRate / 200);
            foreach (var dev in _inputDevices)
            {
                dev.RingBuffer = new RingBuffer(ringFrames, dev.Info.Channels);
                dev.InputOverflowCount = 0;
                dev.PeakLevels = new float[dev.Info.Channels];
                dev.InputAsrc = null;
                PrepareCaptureTransport(dev);
            }

            // ===== 3. Render transports prepared (periods + depths become known) =====
            var preparedOutputs = new List<ActiveDevice>();
            foreach (var dev in _outputDevices)
            {
                if (PrepareRenderTransport(dev, marginMs))
                {
                    preparedOutputs.Add(dev);
                }
            }

            if (preparedOutputs.Count == 0)
            {
                Stop();
                return false;
            }

            // If the preferred master could not be opened, promote a live output as runtime
            // master. Providers convert by ratio, so audio stays correct even if the engine
            // rate is no longer 1:1 with the promoted device.
            if (!preparedOutputs.Contains(masterOutput))
            {
                masterOutput = preparedOutputs[0];
                foreach (var d in _outputDevices)
                {
                    d.IsMasterDevice = d.Info.Id == masterOutput.Info.Id;
                }
            }

            // ===== 4. Per-input ring fill targets from real periods + margin =====
            foreach (var dev in _inputDevices)
            {
                dev.FillTargetFrames = CalculateInputFillTargetFrames(dev, masterOutput, marginMs);
            }

            // ===== 5. Coordinator =====
            _syncCoordinator = new OutputSyncCoordinator(masterOutput.Info.Id, _engineSampleRate);
            _syncCoordinator.ArmGlobalRefillHold();

            var sources = _inputDevices
                .Where(d => d.RingBuffer != null)
                .Select(d => new MixingSampleProvider.CaptureSource(
                    d.Info.Id,
                    d.RingBuffer!,
                    d.GlobalChannelOffset,
                    d.Info.Channels,
                    d.IsMasterDevice,
                    d.FillTargetFrames))
                .ToList();

            // ===== 6. Providers bound to renders (consumer cursors pinned on EMPTY rings) =====
            var startedOutputs = new List<ActiveDevice>();
            foreach (var dev in preparedOutputs)
            {
                dev.ConsumerId = dev.Info.Id;
                dev.MixProvider = new MixingSampleProvider(
                    _routingMatrix, sources,
                    dev.GlobalChannelOffset,
                    dev.Info.Channels,
                    dev.Info.SampleRate,
                    dev.OutputDelayMs,
                    dev.ConsumerId,
                    _syncCoordinator);

                if (!BindRenderTransport(dev))
                {
                    try { dev.MixProvider?.DetachConsumer(); } catch { }
                    dev.MixProvider = null;
                    dev.ConsumerId = string.Empty;
                    ReleaseRenderTransport(dev);
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

            if (!startedOutputs.Any(d => d.Info.Id == masterOutput.Info.Id))
            {
                var runtimeMaster = startedOutputs[0];
                foreach (var d in _outputDevices)
                {
                    d.IsMasterDevice = d.Info.Id == runtimeMaster.Info.Id;
                }

                _syncCoordinator.SetMasterConsumer(runtimeMaster.Info.Id);
                _syncCoordinator.ArmGlobalRefillHold();
                masterOutput = runtimeMaster;
            }

            // Preferred consumer for ring trim ordering + the input ASRC fill reference.
            ApplyPreferredMasterConsumerToInputs();

            // ===== 7. Start captures (rings begin filling; cursors already pinned) =====
            foreach (var dev in _inputDevices)
            {
                if (dev.RingBuffer == null) continue;
                StartCaptureTransport(dev, masterOutput.Info.Id);
            }

            // ===== 8. Play all outputs together =====
            foreach (var dev in startedOutputs)
            {
                PlayRenderTransport(dev);
            }

            _running = true;
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

    /// <summary>Applies persisted buffering settings without restarting (config load).</summary>
    public void ConfigureBuffering(int standardMarginMs, int highPerfMarginMs, bool highPerformanceMode)
    {
        _standardMarginMs = Math.Clamp(standardMarginMs, MarginMinMs, MarginMaxMs);
        _highPerfMarginMs = Math.Clamp(highPerfMarginMs, MarginMinMs, MarginMaxMs);
        _highPerformanceMode = highPerformanceMode;
    }

    /// <summary>Sets the safety margin of the ACTIVE mode. Restarts the pipeline if running.</summary>
    public bool SetBufferMarginMs(int marginMs)
    {
        int clamped = Math.Clamp(marginMs, MarginMinMs, MarginMaxMs);
        if (BufferMarginMs == clamped)
        {
            return true;
        }

        if (_highPerformanceMode) _highPerfMarginMs = clamped;
        else _standardMarginMs = clamped;

        if (_running)
        {
            FullRestart();
        }

        StateChanged?.Invoke();
        return true;
    }

    /// <summary>Switches transport mode. Restarts the pipeline if running.</summary>
    public bool SetHighPerformanceMode(bool enabled)
    {
        if (_highPerformanceMode == enabled)
        {
            return true;
        }

        _highPerformanceMode = enabled;

        if (_running)
        {
            FullRestart();
        }

        StateChanged?.Invoke();
        return true;
    }

    // ===== Transport plumbing =====

    private static string ResolveEndpointId(ActiveDevice dev)
    {
        return dev.IsLoopback && dev.Info.Id.StartsWith("loop:", StringComparison.Ordinal)
            ? dev.Info.Id.Substring("loop:".Length)
            : dev.Info.Id;
    }

    private static double GetDefaultPeriodMs(MMDevice mmDevice)
    {
        try
        {
            long period100ns = mmDevice.AudioClient.DefaultDevicePeriod;
            if (period100ns > 0) return period100ns / 10_000.0;
        }
        catch
        {
        }
        return FallbackPeriodMs;
    }

    /// <summary>
    /// Opens (but does not start) the capture transport for <paramref name="dev"/> so its
    /// callback period is known before fill targets are computed. In high-performance mode
    /// the own WASAPI client is tried first and the standard transport is the fallback.
    /// </summary>
    private void PrepareCaptureTransport(ActiveDevice dev)
    {
        ReleaseCaptureTransport(dev);
        dev.CapturePeriodMs = 0;
        dev.LowLatencyPeriod = false;
        dev.TransportLabel = string.Empty;

        string endpointId = ResolveEndpointId(dev);

        if (_highPerformanceMode)
        {
            var client = new LowLatencyCaptureClient(endpointId, dev.IsLoopback);
            if (client.Initialize() && client.Channels == dev.Info.Channels && client.SampleRate == dev.Info.SampleRate)
            {
                dev.HpCapture = client;
                dev.CapturePeriodMs = client.PeriodMs;
                dev.LowLatencyPeriod = client.LowLatencyPeriod;
                dev.TransportLabel = client.TransportLabel;
                return;
            }
            client.Dispose();
            // Fall through: the device still works on the standard transport.
        }

        var mmDevice = _enumerator.GetDevice(endpointId);
        if (mmDevice == null) return;

        try
        {
            dev.Capture = dev.IsLoopback
                ? new WasapiLoopbackCapture(mmDevice)
                : new WasapiCapture(mmDevice, true, StandardCaptureBufferMs);
            dev.Capture.WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(dev.Info.SampleRate, dev.Info.Channels);

            double period = dev.IsLoopback ? StandardLoopbackPollMs : GetDefaultPeriodMs(mmDevice);
            dev.CapturePeriodMs = period;
            dev.TransportLabel = dev.IsLoopback
                ? $"WASAPI loopback (polled) {period:0.##} ms"
                : $"WASAPI shared {period:0.##} ms period";
        }
        catch
        {
            try { dev.Capture?.Dispose(); } catch { }
            dev.Capture = null;
        }
    }

    /// <summary>Opens (but does not play) the render transport; records period and held depth.</summary>
    private bool PrepareRenderTransport(ActiveDevice dev, int marginMs)
    {
        ReleaseRenderTransport(dev);
        dev.RenderPeriodMs = 0;
        dev.RenderDepthMs = 0;
        dev.LowLatencyPeriod = false;
        dev.TransportLabel = string.Empty;

        if (_highPerformanceMode)
        {
            var client = new LowLatencyRenderClient(dev.Info.Id);
            if (client.Initialize(marginMs) && client.Channels == dev.Info.Channels && client.SampleRate == dev.Info.SampleRate)
            {
                dev.HpRender = client;
                dev.RenderPeriodMs = client.PeriodMs;
                dev.RenderDepthMs = client.DepthMs;
                dev.LowLatencyPeriod = client.LowLatencyPeriod;
                dev.TransportLabel = client.TransportLabel;
                return true;
            }
            client.Dispose();
            // Fall through: the device still works on the standard transport.
        }

        var mmDevice = _enumerator.GetDevice(dev.Info.Id);
        if (mmDevice == null) return false;

        double period = GetDefaultPeriodMs(mmDevice);
        // NAudio keeps its whole buffer topped up, so the requested buffer IS the held depth.
        int depthMs = (int)Math.Ceiling(2 * period + Math.Max(0, marginMs));
        dev.StandardRenderEndpoint = mmDevice;
        dev.RenderPeriodMs = period;
        dev.RenderDepthMs = depthMs;
        dev.TransportLabel = $"WASAPI shared {period:0.##} ms period";
        return true;
    }

    /// <summary>Attaches the provider to the prepared render transport.</summary>
    private bool BindRenderTransport(ActiveDevice dev)
    {
        if (dev.HpRender != null)
        {
            return true; // provider is handed over at play time
        }

        var mmDevice = dev.StandardRenderEndpoint;
        if (mmDevice == null || dev.MixProvider == null) return false;

        try
        {
            var render = new WasapiOut(mmDevice, AudioClientShareMode.Shared, true, (int)Math.Ceiling(dev.RenderDepthMs));
            render.Init(dev.MixProvider);
            dev.Render = render;
            return true;
        }
        catch
        {
            try { dev.Render?.Dispose(); } catch { }
            dev.Render = null;
            return false;
        }
    }

    private void PlayRenderTransport(ActiveDevice dev)
    {
        try
        {
            if (dev.HpRender != null && dev.MixProvider != null)
            {
                dev.HpRender.Start(dev.MixProvider);
            }
            else
            {
                dev.Render?.Play();
            }
        }
        catch
        {
        }
    }

    private bool StartCaptureTransport(ActiveDevice dev, string masterConsumerId)
    {
        if (dev.RingBuffer == null) return false;
        if (dev.Capture == null && dev.HpCapture == null) return false;

        // Capture-side ASRC: convert this device's stream into the engine clock domain.
        // Handles BOTH static rate mismatch (e.g. 44.1k capture → 48k engine) and crystal
        // drift (fill-level PI), so the rings always contain coherent engine-rate audio.
        var asrc = new InputAsrc(dev.RingBuffer, dev.Info.Channels, dev.Info.SampleRate, _engineSampleRate, dev.FillTargetFrames);
        asrc.SetFillConsumer(masterConsumerId);
        dev.InputAsrc = asrc;

        if (dev.HpCapture != null)
        {
            if (!dev.HpCapture.Start((samples, frames) => ProcessCaptureBlock(dev, samples, frames)))
            {
                ReleaseCaptureTransport(dev);
                dev.InputAsrc = null;
                return false;
            }
            return true;
        }

        int channels = dev.Info.Channels;
        // Reusable scratch for the WASAPI capture thread; avoids per-callback GC pressure
        // (allocation-triggered Gen0 collections were a real source of audio-thread stalls).
        float[] captureScratch = [];
        dev.Capture!.DataAvailable += (s, e) =>
        {
            int floatCount = e.BytesRecorded / 4;
            if (floatCount <= 0) return;
            int frames = floatCount / channels;
            if (frames <= 0) return;

            if (captureScratch.Length < floatCount)
            {
                int newSize = Math.Max(floatCount, Math.Max(64, captureScratch.Length * 2));
                captureScratch = new float[newSize];
            }
            Buffer.BlockCopy(e.Buffer, 0, captureScratch, 0, e.BytesRecorded);
            ProcessCaptureBlock(dev, captureScratch, frames);
        };

        try
        {
            dev.Capture.StartRecording();
        }
        catch
        {
            ReleaseCaptureTransport(dev);
            dev.InputAsrc = null;
            return false;
        }

        return true;
    }

    /// <summary>Common per-block capture path for both transports: peaks, then ASRC → ring.</summary>
    private static void ProcessCaptureBlock(ActiveDevice dev, float[] samples, int frames)
    {
        var deviceAsrc = dev.InputAsrc;
        if (dev.RingBuffer == null || deviceAsrc == null || frames <= 0) return;

        int channels = dev.Info.Channels;
        var peaks = dev.PeakLevels;
        if (peaks != null)
        {
            for (int f = 0; f < frames; f++)
            {
                int baseIdx = f * channels;
                for (int c = 0; c < channels; c++)
                {
                    float v = samples[baseIdx + c];
                    if (v < 0) v = -v;
                    if (v > peaks[c]) peaks[c] = v;
                }
            }
        }

        int written = deviceAsrc.ProcessAndWrite(samples, frames);
        if (written <= 0)
        {
            Interlocked.Increment(ref dev.InputOverflowCount);
        }
    }

    private static void ReleaseCaptureTransport(ActiveDevice dev)
    {
        try { dev.Capture?.StopRecording(); } catch { }
        try { dev.Capture?.Dispose(); } catch { }
        dev.Capture = null;
        try { dev.HpCapture?.Dispose(); } catch { }
        dev.HpCapture = null;
    }

    private static void ReleaseRenderTransport(ActiveDevice dev)
    {
        try { dev.Render?.Stop(); } catch { }
        try { dev.Render?.Dispose(); } catch { }
        dev.Render = null;
        try { dev.HpRender?.Dispose(); } catch { }
        dev.HpRender = null;
        dev.StandardRenderEndpoint = null;
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
            ReleaseCaptureTransport(dev);
            dev.InputAsrc = null;
            dev.RingBuffer?.Clear();
        }

        foreach (var dev in _outputDevices)
        {
            ReleaseRenderTransport(dev);
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

    /// <summary>
    /// Ring fill the servo holds for one input: enough to always serve one render pull
    /// (master render period) after the worst-case capture arrival gap (capture period),
    /// plus the user's safety margin. Bounded well inside ring capacity.
    /// </summary>
    private int CalculateInputFillTargetFrames(ActiveDevice input, ActiveDevice masterOutput, int marginMs)
    {
        double capturePeriod = input.CapturePeriodMs > 0 ? input.CapturePeriodMs : FallbackPeriodMs;
        double renderPeriod = masterOutput.RenderPeriodMs > 0 ? masterOutput.RenderPeriodMs : FallbackPeriodMs;
        double targetMs = Math.Clamp(capturePeriod + renderPeriod + Math.Max(0, marginMs), 2, RingBufferCapacityMs * 3 / 4.0);
        return Math.Max(32, (int)Math.Round(_engineSampleRate * targetMs / 1000.0));
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
                var existing = _dormantRoutes.FirstOrDefault(r =>
                    r.InputDeviceId == inDevice.Info.Id &&
                    r.InputLocalChannel == inLocal &&
                    r.OutputDeviceId == outDevice.Info.Id &&
                    r.OutputLocalChannel == outLocal);

                if (existing == default)
                {
                    _dormantRoutes.Add(new RoutedCrosspoint(
                        inDevice.Info.Id,
                        inLocal,
                        outDevice.Info.Id,
                        outLocal,
                        cp.Active,
                        gainDb));
                }
                else
                {
                    // Update the gain if it changed
                    var index = _dormantRoutes.IndexOf(existing);
                    _dormantRoutes[index] = existing with { GainDb = gainDb };
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
                    gainDb));
            }
        }

        return snapshot;
    }

    private void RestoreRoutedCrosspoints(IEnumerable<RoutedCrosspoint> snapshot)
    {
        _routingMatrix.ClearAll();

        foreach (var route in snapshot)
        {
            var inDevice = _inputDevices.FirstOrDefault(d => d.Info.Id == route.InputDeviceId);
            var outDevice = _outputDevices.FirstOrDefault(d => d.Info.Id == route.OutputDeviceId);
            if (inDevice == null || outDevice == null) continue;

            if (route.InputLocalChannel < 0 || route.InputLocalChannel >= inDevice.Info.Channels) continue;
            if (route.OutputLocalChannel < 0 || route.OutputLocalChannel >= outDevice.Info.Channels) continue;

            int inGlobal = inDevice.GlobalChannelOffset + route.InputLocalChannel;
            int outGlobal = outDevice.GlobalChannelOffset + route.OutputLocalChannel;
            _routingMatrix.SetCrosspoint(inGlobal, outGlobal, route.Active, route.GainDb);
        }

        _routingMatrix.Publish();
    }

    public void Dispose()
    {
        Stop();
        _enumerator.Dispose();
    }
}

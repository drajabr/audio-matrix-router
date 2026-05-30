using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Threading;

namespace AudioMatrixRouter.Audio;

public class ActiveDevice
{
    public required DeviceInfo Info { get; init; }
    public int GlobalChannelOffset { get; set; }
    public RingBuffer? RingBuffer { get; set; }
    public WasapiCapture? Capture { get; set; }
    public WasapiOut? Render { get; set; }
    public MixingSampleProvider? MixProvider { get; set; }
    public bool IsMasterDevice { get; set; }
    public int OutputDelayMs { get; set; }
    public string ConsumerId { get; set; } = string.Empty;
    public long InputOverflowCount;
    public int CaptureLatencyMs { get; set; }
    public int RenderLatencyMs { get; set; }
    public bool IsLoopback { get; set; }
    public double BaseLatencyMs { get; set; }
    // Per-channel running peak (0..1). Producer writes; UI samples and resets atomically.
    public float[]? PeakLevels;

}

public class AudioEngine : IDisposable
{
    private const int DefaultInputRingBufferMs = 80;
    private const int DefaultOutputBufferMs = 100;
    // Keep ring buffers at a stable ceiling to avoid producer/consumer rewire glitches at runtime.
    // The input buffer slider controls WASAPI capture period, while rings provide spike headroom.
    private const int RingBufferCapacityMs = 400;
    private const int RenderPeriodMs = 10;

    private readonly DeviceEnumerator _enumerator = new();
    private readonly List<ActiveDevice> _inputDevices = [];
    private readonly List<ActiveDevice> _outputDevices = [];
    private readonly RoutingMatrix _routingMatrix = new();
    private bool _running;
    private OutputSyncCoordinator? _syncCoordinator;
    private int _inputBufferMs = DefaultInputRingBufferMs;
    private int _outputBufferMs = DefaultOutputBufferMs;
    
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
    public int InputBufferMs => _inputBufferMs;
    public int OutputBufferMs => _outputBufferMs;

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
        double captureQueueMs = input.Info.SampleRate > 0
            ? (queuedFrames * 1000.0) / input.Info.SampleRate
            : 0;

        // Real driver latencies queried at Start(); fall back to the requested period if unavailable.
        int captureDriverMs = input.CaptureLatencyMs > 0 ? input.CaptureLatencyMs : _inputBufferMs;
        int renderDriverMs = output.RenderLatencyMs > 0 ? output.RenderLatencyMs : RenderPeriodMs;

        latencyMs = captureDriverMs + captureQueueMs + renderDriverMs + output.OutputDelayMs;
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

        int captureDriverMs = inputMaster.CaptureLatencyMs > 0 ? inputMaster.CaptureLatencyMs : _inputBufferMs;

        // The capture-to-render ring queue is the variable buffering the sync controller actively
        // moves around. Without it the displayed latency is just the static driver constant and
        // never reflects what the buffer is actually doing.
        double queueMs = 0;
        if (inputMaster.Info.SampleRate > 0)
        {
            int queuedFrames = inputMaster.RingBuffer.GetAvailableFrames(consumerId);
            queueMs = (queuedFrames * 1000.0) / inputMaster.Info.SampleRate;
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

    public void Init()
    {
        _enumerator.SetChangeCallback(() => DevicesChanged?.Invoke());
    }

    public bool SetInputMasterDevice(string deviceId)
    {
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

            if (_running)
            {
                foreach (var output in _outputDevices)
                {
                    output.MixProvider?.SetInputMasterDevice(string.Empty);
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

        if (_running)
        {
            foreach (var output in _outputDevices)
            {
                output.MixProvider?.SetInputMasterDevice(deviceId);
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

            _syncCoordinator?.SetMasterConsumer(string.Empty);
            ApplyPreferredMasterConsumerToInputs();

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

        _syncCoordinator?.SetMasterConsumer(deviceId);
        ApplyPreferredMasterConsumerToInputs();

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
        _inputDevices.Add(ad);
        RecalcChannelOffsets();

        RestoreDormantRoutesForInputDevice(deviceId);

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
        _outputDevices.Add(ad);
        RecalcChannelOffsets();

        RestoreDormantRoutesForOutputDevice(deviceId);

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
        
        // Capture routes for this device before removing it
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
        
        // Capture routes for this device before removing it
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

            // The mixer reads source-rate samples block-by-block at the output's nominal rate
            // without resampling. If a routed input runs at a different sample rate than its
            // destination output, the result is pitch-shifted audio. Surface this loudly in the
            // debug log so it can be diagnosed from field reports; the engine still starts so
            // matched-rate routes keep working.
            foreach (var outDev in _outputDevices)
            {
                foreach (var inDev in _inputDevices)
                {
                    if (inDev.Info.SampleRate > 0
                        && outDev.Info.SampleRate > 0
                        && inDev.Info.SampleRate != outDev.Info.SampleRate)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[AudioEngine] WARNING: sample-rate mismatch — input '{inDev.Info.Name}' @ {inDev.Info.SampleRate} Hz routed to output '{outDev.Info.Name}' @ {outDev.Info.SampleRate} Hz will play at the wrong pitch (no SRC).");
                    }
                }
            }

            // Setup captures
            foreach (var dev in _inputDevices)
            {
                var mmDevice = _enumerator.GetDevice(dev.IsLoopback && dev.Info.Id.StartsWith("loop:", StringComparison.Ordinal)
                    ? dev.Info.Id.Substring("loop:".Length)
                    : dev.Info.Id);
                if (mmDevice == null) continue;

                // Allocate a stable ring once per run; avoid runtime ring object swaps.
                int ringBufferMs = RingBufferCapacityMs;
                int ringFrames = Math.Max(dev.Info.SampleRate * ringBufferMs / 1000, dev.Info.SampleRate / 200);
                dev.RingBuffer = new RingBuffer(ringFrames, dev.Info.Channels);
                dev.InputOverflowCount = 0;
                dev.PeakLevels = new float[dev.Info.Channels];
                if (!CreateAndStartCapture(dev))
                {
                    continue;
                }
            }

            var (baseMasterTargetFrames, maxMasterTargetFrames) = CalculateSyncTargetFrames(masterOutput);
            _syncCoordinator = new OutputSyncCoordinator(masterOutput.Info.Id, baseMasterTargetFrames, maxMasterTargetFrames);

            var sources = _inputDevices
                .Where(d => d.RingBuffer != null)
                .Select(d => new MixingSampleProvider.CaptureSource(
                    d.Info.Id,
                    d.RingBuffer!,
                    d.GlobalChannelOffset,
                    d.Info.Channels,
                    d.IsMasterDevice))
                .ToList();

            // Start render
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
                var inputMaster = GetInputMasterDevice();
                if (inputMaster != null)
                {
                    dev.MixProvider.SetInputMasterDevice(inputMaster.Info.Id);
                }

                if (!TryInitRender(dev, mmDevice, _outputBufferMs))
                {
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

            // If the preferred master failed to initialize, promote a live output as runtime master.
            if (!startedOutputs.Any(d => d.Info.Id == masterOutput.Info.Id))
            {
                var runtimeMaster = startedOutputs[0];
                foreach (var d in _outputDevices)
                {
                    d.IsMasterDevice = d.Info.Id == runtimeMaster.Info.Id;
                }

                _syncCoordinator?.SetMasterConsumer(runtimeMaster.Info.Id);
                var (runtimeBaseTargetFrames, runtimeMaxTargetFrames) = CalculateSyncTargetFrames(runtimeMaster);
                _syncCoordinator?.SetMasterBufferTarget(runtimeBaseTargetFrames, runtimeMaxTargetFrames);
            }

            // Play all outputs together after all are initialized to minimize startup cursor skew.
            foreach (var dev in _outputDevices)
            {
                try { dev.Render?.Play(); } catch { }
            }

            ApplyPreferredMasterConsumerToInputs();

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

        UpdateSyncBufferTargets();

        StateChanged?.Invoke();
        return true;
    }

    private bool TryInitRender(ActiveDevice dev, MMDevice mmDevice, int latencyMs)
    {
        try
        {
            var render = new WasapiOut(mmDevice, AudioClientShareMode.Shared, true, latencyMs);
            render.Init(dev.MixProvider!);
            dev.Render = render;
            dev.RenderLatencyMs = latencyMs;
            return true;
        }
        catch
        {
            try { dev.Render?.Dispose(); } catch { }
            dev.Render = null;
            return false;
        }
    }

    private bool CreateAndStartCapture(ActiveDevice dev)
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

        if (dev.IsLoopback)
        {
            dev.Capture = new WasapiLoopbackCapture(mmDevice);
        }
        else
        {
            dev.Capture = new WasapiCapture(mmDevice, true, _inputBufferMs);
        }

        dev.Capture.WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(dev.Info.SampleRate, dev.Info.Channels);
        int channels = dev.Info.Channels;
        // Reusable scratch for the WASAPI capture thread. NAudio's WasapiCapture exposes
        // its packed-byte buffer via DataAvailable; we have to convert to float32 to fan out
        // through the matrix. Allocating a fresh float[] every callback creates ~100 GC-tier
        // allocations/sec per capture device, which under a game's CPU pressure causes Gen0
        // collections that briefly stall the audio thread → overrun → ring trim → audible glitch.
        // The scratch is owned only by this DataAvailable handler (single-threaded by NAudio).
        float[] captureScratch = [];
        dev.Capture.DataAvailable += (s, e) =>
        {
            if (dev.RingBuffer == null)
            {
                return;
            }

            int floatCount = e.BytesRecorded / 4;
            if (floatCount <= 0) return;
            int frames = floatCount / channels;

            if (captureScratch.Length < floatCount)
            {
                // Grow with headroom so common jitter (callback delivering 2x frames after a stall)
                // doesn't reallocate. Power-of-two-ish growth is fine.
                int newSize = Math.Max(floatCount, captureScratch.Length * 2);
                captureScratch = new float[newSize];
            }
            Buffer.BlockCopy(e.Buffer, 0, captureScratch, 0, e.BytesRecorded);

            var peaks = dev.PeakLevels;
            if (peaks != null)
            {
                for (int f = 0; f < frames; f++)
                {
                    int baseIdx = f * channels;
                    for (int c = 0; c < channels; c++)
                    {
                        float v = captureScratch[baseIdx + c];
                        if (v < 0) v = -v;
                        if (v > peaks[c]) peaks[c] = v;
                    }
                }
            }

            if (!dev.RingBuffer.Write(captureScratch, 0, frames))
            {
                Interlocked.Increment(ref dev.InputOverflowCount);
            }
        };

        dev.Capture.StartRecording();
        dev.CaptureLatencyMs = _inputBufferMs;
        return true;
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

        UpdateSyncBufferTargets();

        StateChanged?.Invoke();
        return true;
    }

    private void FullRestart()
    {
        Stop();
        Start();
    }

    private void StopAllCapturesNoThrow()
    {
        foreach (var dev in _inputDevices)
        {
            try { dev.Capture?.StopRecording(); } catch { }
            try { dev.Capture?.Dispose(); } catch { }
            dev.Capture = null;
        }
    }

    private void StopAllRendersNoThrow()
    {
        foreach (var dev in _outputDevices)
        {
            try { dev.Render?.Stop(); } catch { }
            try { dev.Render?.Dispose(); } catch { }
            dev.Render = null;
        }
    }

    public void Stop()
    {
        foreach (var dev in _inputDevices)
        {
            try { dev.Capture?.StopRecording(); } catch { }
            try { dev.Capture?.Dispose(); } catch { }
            dev.Capture = null;
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
        }
    }

    private void UpdateSyncBufferTargets()
    {
        if (_syncCoordinator == null) return;

        var masterOutput = GetOutputMasterDevice() ?? _outputDevices.FirstOrDefault();
        if (masterOutput == null) return;

        var (baseMasterTargetFrames, maxMasterTargetFrames) = CalculateSyncTargetFrames(masterOutput);
        _syncCoordinator.SetMasterBufferTarget(baseMasterTargetFrames, maxMasterTargetFrames);
    }

    private (int BaseMasterTargetFrames, int MaxMasterTargetFrames) CalculateSyncTargetFrames(ActiveDevice? masterOutput)
    {
        int sampleRate = masterOutput?.Info.SampleRate
            ?? _inputDevices.FirstOrDefault()?.Info.SampleRate
            ?? 48000;

        int desiredByOutputBufferFrames = Math.Max(sampleRate * _outputBufferMs / 1000, sampleRate / 200);

        // Use actual ring capacity from live ring buffers (stable 400ms buffers).
        int projectedRingCapacityFrames = sampleRate * RingBufferCapacityMs / 1000;
        int minRingCapacityFrames = _inputDevices
            .Where(d => d.RingBuffer != null)
            .Select(d => d.RingBuffer!.CapacityFrames)
            .DefaultIfEmpty(projectedRingCapacityFrames)
            .Min();

        // Reserve some headroom before the ring overflows: allow up to 80% of ring capacity.
        int maxSafeTargetFrames = Math.Max(64, (minRingCapacityFrames * 4) / 5);
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

        if (removedInputIds.Count == 0 || removedOutputIds.Count == 0)
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

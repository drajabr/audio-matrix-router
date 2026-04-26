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
    private const int DefaultInputRingBufferMs = 40;
    private const int DefaultOutputBufferMs = 40;
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

    private readonly record struct RoutedCrosspoint(
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

        int queuedFrames = inputMaster.RingBuffer.GetAvailableFrames(consumerId);
        double captureQueueMs = inputMaster.Info.SampleRate > 0
            ? (queuedFrames * 1000.0) / inputMaster.Info.SampleRate
            : 0;

        int captureDriverMs = inputMaster.CaptureLatencyMs > 0 ? inputMaster.CaptureLatencyMs : _inputBufferMs;
        latencyMs = captureDriverMs + captureQueueMs;
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
        ApplyPersistedOutputBaseLatenciesToRunningSync();
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
        StateChanged?.Invoke();
        return true;
    }

    public bool AddOutputDevice(string deviceId)
    {
        if (_outputDevices.Any(d => d.Info.Id == deviceId)) return false;

        var devices = _enumerator.GetDevices(DataFlow.Render);
        var found = devices.FirstOrDefault(d => d.Id == deviceId);
        if (found == null) return false;

        var ad = new ActiveDevice { Info = found };
        _outputDevices.Add(ad);
        RecalcChannelOffsets();
        StateChanged?.Invoke();
        return true;
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
        var routeSnapshot = CaptureRoutedCrosspoints();
        bool wasRunning = _running;
        if (wasRunning) Stop();
        _outputDevices.RemoveAt(index);
        RecalcChannelOffsets();
        RestoreRoutedCrosspoints(routeSnapshot);
        if (wasRunning && _inputDevices.Count > 0 && _outputDevices.Count > 0 && _routingMatrix.HasAnyCrosspoints()) Start();
        StateChanged?.Invoke();
    }

    public void SetCrosspoint(int inCh, int outCh, bool active, float gainDb)
    {
        bool changed = _routingMatrix.SetCrosspoint(inCh, outCh, active, gainDb);
        if (!changed)
        {
            return;
        }

        _routingMatrix.Publish();
        StateChanged?.Invoke();
    }

    public int SetCrosspoints(IEnumerable<(int InCh, int OutCh, bool Active, float GainDb)> updates)
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
                    dev.Info.Id == masterOutput.Info.Id ? 0 : dev.BaseLatencyMs,
                    dev.ConsumerId,
                    _syncCoordinator);
                var inputMaster = GetInputMasterDevice();
                if (inputMaster != null)
                {
                    dev.MixProvider.SetInputMasterDevice(inputMaster.Info.Id);
                }

                dev.Render = new WasapiOut(mmDevice, AudioClientShareMode.Shared, true, _outputBufferMs);
                dev.Render.Init(dev.MixProvider);
                dev.Render.Play();
                dev.RenderLatencyMs = _outputBufferMs;
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

        // If running, recreate capture clients so the new latency period actually applies.
        if (_running)
        {
            RestartInputCapturesForNewLatency();
        }

        // Recalculate targets because capture cadence changed.
        UpdateSyncBufferTargets();

        StateChanged?.Invoke();
        return true;
    }

    private void RestartInputCapturesForNewLatency()
    {
        foreach (var dev in _inputDevices)
        {
            try { dev.Capture?.StopRecording(); } catch { }
            try { dev.Capture?.Dispose(); } catch { }
            dev.Capture = null;
        }

        // Keep existing ring instances (consumers reference these objects).
        // Clear once so the new capture period starts from a clean queue.
        foreach (var dev in _inputDevices)
        {
            dev.RingBuffer?.Clear();
            dev.InputOverflowCount = 0;
            CreateAndStartCapture(dev);
        }

        // Recalculate sync targets with new ring buffer sizes
        var masterOutput = GetOutputMasterDevice();
        if (masterOutput != null)
        {
            var (baseMasterTargetFrames, maxMasterTargetFrames) = CalculateSyncTargetFrames(masterOutput);
            _syncCoordinator?.SetMasterBufferTarget(baseMasterTargetFrames, maxMasterTargetFrames);
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
        dev.Capture.DataAvailable += (s, e) =>
        {
            if (dev.RingBuffer == null)
            {
                return;
            }

            int floatCount = e.BytesRecorded / 4;
            int frames = floatCount / channels;
            var floats = new float[floatCount];
            Buffer.BlockCopy(e.Buffer, 0, floats, 0, e.BytesRecorded);

            var peaks = dev.PeakLevels;
            if (peaks != null)
            {
                for (int f = 0; f < frames; f++)
                {
                    int baseIdx = f * channels;
                    for (int c = 0; c < channels; c++)
                    {
                        float v = floats[baseIdx + c];
                        if (v < 0) v = -v;
                        if (v > peaks[c]) peaks[c] = v;
                    }
                }
            }

            if (!dev.RingBuffer.Write(floats, 0, frames))
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

        // If running, restart render clients so the hardware period reflects the new value.
        // Software max, hardware min: the wheel sets both the software target ceiling
        // and the WASAPI render period floor so driver-level buffering scales with the setting.
        if (_running)
        {
            RestartRenderClientsForNewPeriod();
        }

        UpdateSyncBufferTargets();

        StateChanged?.Invoke();
        return true;
    }

    private void RestartRenderClientsForNewPeriod()
    {
        foreach (var dev in _outputDevices)
        {
            try { dev.Render?.Stop(); } catch { }
            try { dev.Render?.Dispose(); } catch { }
            dev.Render = null;

            if (dev.MixProvider == null) continue;

            var mmDevice = _enumerator.GetDevice(dev.Info.Id);
            if (mmDevice == null) continue;

            dev.Render = new WasapiOut(mmDevice, AudioClientShareMode.Shared, true, _outputBufferMs);
            dev.Render.Init(dev.MixProvider);
            dev.Render.Play();
            dev.RenderLatencyMs = _outputBufferMs;
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
            // BaseLatencyMs is now read-only (no learned bias adjustment)
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

    private void ApplyPersistedOutputBaseLatenciesToRunningSync()
    {
        var masterOutput = GetOutputMasterDevice();
        foreach (var device in _outputDevices)
        {
            double baseLatencyMs = (masterOutput != null && device.Info.Id == masterOutput.Info.Id)
                ? 0
                : device.BaseLatencyMs;
            // No longer setting base latency (learned bias removed from sync logic)
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

        static bool IsInputStillAvailable(ActiveDevice input, List<DeviceInfo> captures) =>
            captures.Any(c => c.Id == input.Info.Id);

        bool changed = _inputDevices.Any(d => !IsInputStillAvailable(d, captureDevices))
            || _outputDevices.Any(d => !renderDevices.Any(rd => rd.Id == d.Info.Id));

        if (!changed)
        {
            return;
        }

        var routeSnapshot = CaptureRoutedCrosspoints();
        bool wasRunning = _running;
        if (wasRunning)
        {
            Stop();
        }

        for (int i = _inputDevices.Count - 1; i >= 0; i--)
        {
            if (!IsInputStillAvailable(_inputDevices[i], captureDevices))
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

        if (wasRunning && _inputDevices.Count > 0 && _outputDevices.Count > 0 && _routingMatrix.HasAnyCrosspoints())
        {
            Start();
        }

        StateChanged?.Invoke();
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

using NAudio.CoreAudioApi;
using NAudio.Wave;

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
}

public class AudioEngine : IDisposable
{
    private readonly DeviceEnumerator _enumerator = new();
    private readonly List<ActiveDevice> _inputDevices = [];
    private readonly List<ActiveDevice> _outputDevices = [];
    private readonly RoutingMatrix _routingMatrix = new();
    private bool _running;

    public event Action? DevicesChanged;
    public event Action? StateChanged;

    public IReadOnlyList<ActiveDevice> InputDevices => _inputDevices;
    public IReadOnlyList<ActiveDevice> OutputDevices => _outputDevices;
    public RoutingMatrix RoutingMatrix => _routingMatrix;
    public bool IsRunning => _running;
    public DeviceEnumerator Enumerator => _enumerator;

    public int TotalInputChannels { get; private set; }
    public int TotalOutputChannels { get; private set; }

    public void Init()
    {
        _enumerator.SetChangeCallback(() => DevicesChanged?.Invoke());
    }

    public bool SetInputMasterDevice(string deviceId)
    {
        var device = _inputDevices.FirstOrDefault(d => d.Info.Id == deviceId);
        if (device == null) return false;

        foreach (var d in _inputDevices) d.IsMasterDevice = false;
        device.IsMasterDevice = true;
        StateChanged?.Invoke();
        return true;
    }

    public bool SetOutputMasterDevice(string deviceId)
    {
        var device = _outputDevices.FirstOrDefault(d => d.Info.Id == deviceId);
        if (device == null) return false;

        foreach (var d in _outputDevices) d.IsMasterDevice = false;
        device.IsMasterDevice = true;
        StateChanged?.Invoke();
        return true;
    }

    public ActiveDevice? GetInputMasterDevice() =>
        _inputDevices.FirstOrDefault(d => d.IsMasterDevice) ??
        _inputDevices.FirstOrDefault();

    public ActiveDevice? GetOutputMasterDevice() =>
        _outputDevices.FirstOrDefault(d => d.IsMasterDevice) ??
        _outputDevices.FirstOrDefault();

    public List<DeviceInfo> GetAvailableDevices(DataFlow flow) => _enumerator.GetDevices(flow);

    public bool AddInputDevice(string deviceId)
    {
        if (_inputDevices.Any(d => d.Info.Id == deviceId)) return false;

        var devices = _enumerator.GetDevices(DataFlow.Capture);
        var found = devices.FirstOrDefault(d => d.Id == deviceId);
        if (found == null) return false;

        var ad = new ActiveDevice { Info = found };
        _inputDevices.Add(ad);
        RecalcChannelOffsets();
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
        bool wasRunning = _running;
        if (wasRunning) Stop();
        _inputDevices.RemoveAt(index);
        RecalcChannelOffsets();
        if (wasRunning && _inputDevices.Count > 0 && _outputDevices.Count > 0) Start();
    }

    public void RemoveOutputDevice(int index)
    {
        if (index < 0 || index >= _outputDevices.Count) return;
        bool wasRunning = _running;
        if (wasRunning) Stop();
        _outputDevices.RemoveAt(index);
        RecalcChannelOffsets();
        if (wasRunning && _inputDevices.Count > 0 && _outputDevices.Count > 0) Start();
    }

    public void SetCrosspoint(int inCh, int outCh, bool active, float gainDb)
    {
        _routingMatrix.SetCrosspoint(inCh, outCh, active, gainDb);

        if (active)
        {
            if (!_inputDevices.Any(d => d.IsMasterDevice))
            {
                var inputDevice = FindInputDeviceByChannel(inCh);
                if (inputDevice != null)
                {
                    SetInputMasterDevice(inputDevice.Info.Id);
                }
            }

            if (!_outputDevices.Any(d => d.IsMasterDevice))
            {
                var outputDevice = FindOutputDeviceByChannel(outCh);
                if (outputDevice != null)
                {
                    SetOutputMasterDevice(outputDevice.Info.Id);
                }
            }
        }

        _routingMatrix.Publish();
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
    }

    public void ClearCrosspoints()
    {
        _routingMatrix.ClearAll();
    }

    public bool Start()
    {
        if (_running) return true;
        if (_inputDevices.Count == 0 || _outputDevices.Count == 0) return false;

        try
        {
            // Setup captures
            foreach (var dev in _inputDevices)
            {
                var mmDevice = _enumerator.GetDevice(dev.Info.Id);
                if (mmDevice == null) continue;

                dev.RingBuffer = new RingBuffer(dev.Info.SampleRate * 2, dev.Info.Channels);
                dev.Capture = new WasapiCapture(mmDevice, true, 10); // 10ms buffer, event-driven
                dev.Capture.WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(dev.Info.SampleRate, dev.Info.Channels);
                dev.Capture.DataAvailable += (s, e) =>
                {
                    // Convert bytes to floats and push to ring buffer
                    int floatCount = e.BytesRecorded / 4;
                    int frames = floatCount / dev.Info.Channels;
                    var floats = new float[floatCount];
                    Buffer.BlockCopy(e.Buffer, 0, floats, 0, e.BytesRecorded);
                    dev.RingBuffer.Write(floats, 0, frames);
                };
                dev.Capture.StartRecording();
            }

            var sources = _inputDevices
                .Where(d => d.RingBuffer != null)
                .Select(d => new MixingSampleProvider.CaptureSource(d.RingBuffer!, d.GlobalChannelOffset, d.Info.Channels))
                .ToList();

            // Start render
            foreach (var dev in _outputDevices)
            {
                var mmDevice = _enumerator.GetDevice(dev.Info.Id);
                if (mmDevice == null) continue;

                dev.MixProvider = new MixingSampleProvider(
                    _routingMatrix, sources,
                    dev.GlobalChannelOffset, dev.Info.Channels, dev.Info.SampleRate);

                dev.Render = new WasapiOut(mmDevice, AudioClientShareMode.Shared, true, 10);
                dev.Render.Init(dev.MixProvider);
                dev.Render.Play();
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
            dev.MixProvider = null;
        }

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

    public void RefreshDevices()
    {
        // Remove devices that no longer exist
        var captureDevices = _enumerator.GetDevices(DataFlow.Capture);
        var renderDevices = _enumerator.GetDevices(DataFlow.Render);

        bool changed = false;
        for (int i = _inputDevices.Count - 1; i >= 0; i--)
        {
            if (!captureDevices.Any(d => d.Id == _inputDevices[i].Info.Id))
            {
                _inputDevices.RemoveAt(i);
                changed = true;
            }
        }
        for (int i = _outputDevices.Count - 1; i >= 0; i--)
        {
            if (!renderDevices.Any(d => d.Id == _outputDevices[i].Info.Id))
            {
                _outputDevices.RemoveAt(i);
                changed = true;
            }
        }

        if (changed) RecalcChannelOffsets();
    }

    public void Dispose()
    {
        Stop();
        _enumerator.Dispose();
    }
}

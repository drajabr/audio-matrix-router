using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Threading;

namespace AudioMatrixRouter.Audio;

public class EventDrivenCaptureDevice
{
    private readonly DeviceEnumerator _enumerator;
    private WasapiCapture? _capture;
    private float[] _captureScratch = Array.Empty<float>();

    public ActiveDevice Device { get; }

    public EventDrivenCaptureDevice(ActiveDevice device, DeviceEnumerator enumerator)
    {
        Device = device;
        _enumerator = enumerator;
    }

    public bool Start(int inputBufferMs)
    {
        if (Device.RingBuffer == null)
        {
            return false;
        }

        var mmDevice = _enumerator.GetDevice(Device.IsLoopback && Device.Info.Id.StartsWith("loop:", StringComparison.Ordinal)
            ? Device.Info.Id.Substring("loop:".Length)
            : Device.Info.Id);
        if (mmDevice == null)
        {
            return false;
        }

        try
        {
            _capture = Device.IsLoopback
                ? new WasapiLoopbackCapture(mmDevice)
                : new WasapiCapture(mmDevice, true, inputBufferMs);

            _capture.WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(Device.Info.SampleRate, Device.Info.Channels);
            _capture.DataAvailable += OnDataAvailable;
            _capture.StartRecording();
            Device.Capture = _capture;
            Device.CaptureLatencyMs = inputBufferMs;
            return true;
        }
        catch
        {
            try { _capture?.Dispose(); } catch { }
            _capture = null;
            Device.Capture = null;
            return false;
        }
    }

    public void Stop()
    {
        try { _capture?.StopRecording(); } catch { }
        try { _capture?.Dispose(); } catch { }
        _capture = null;
        Device.Capture = null;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (Device.RingBuffer == null)
        {
            return;
        }

        int floatCount = e.BytesRecorded / 4;
        if (floatCount <= 0) return;

        int channels = Device.Info.Channels;
        int frames = floatCount / channels;

        if (_captureScratch.Length < floatCount)
        {
            int newSize = Math.Max(floatCount, Math.Max(1, _captureScratch.Length * 2));
            _captureScratch = new float[newSize];
        }

        Buffer.BlockCopy(e.Buffer, 0, _captureScratch, 0, e.BytesRecorded);

        var peaks = Device.PeakLevels;
        if (peaks != null)
        {
            for (int f = 0; f < frames; f++)
            {
                int baseIdx = f * channels;
                for (int c = 0; c < channels; c++)
                {
                    float v = _captureScratch[baseIdx + c];
                    if (v < 0) v = -v;
                    if (v > peaks[c]) peaks[c] = v;
                }
            }
        }

        if (!Device.RingBuffer.Write(_captureScratch, 0, frames))
        {
            Interlocked.Increment(ref Device.InputOverflowCount);
        }
    }
}

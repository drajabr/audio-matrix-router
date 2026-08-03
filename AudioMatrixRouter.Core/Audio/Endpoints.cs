using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioMatrixRouter.Audio;

/// <summary>Interleaved float32 capture delivery. The buffer is only valid for the
/// duration of the callback (reused scratch).</summary>
public delegate void CaptureDataHandler(float[] interleaved, int frames);

/// <summary>Period tier an endpoint runs at (the fallback/degrade ladder).</summary>
public enum EndpointTier
{
    MinPeriod,      // IAudioClient3 minimum shared period
    DoublePeriod,   // 2x minimum (first degrade rung)
    DefaultPeriod,  // classic default-period event stream
    DefaultPolled,  // polled loopback at the default period
    Legacy,         // NAudio backend
}

public interface IRenderEndpoint : IDisposable
{
    void Init(ISampleProvider provider);
    void Play();
    void Stop();
    /// <summary>Real FIFO depth (read back), ms. 0 = unknown.</summary>
    int ActualBufferMs { get; }
    /// <summary>Real engine period (read back), ms. 0 = unknown.</summary>
    double ActualPeriodMs { get; }
    EndpointTier Tier { get; }
    /// <summary>Mid-stream death (device invalidated / stalled). Fired at most once,
    /// from the audio thread - subscribers must marshal, never restart inline.</summary>
    event Action<int>? Faulted;
}

public interface ICaptureEndpoint : IDisposable
{
    void Start();
    void Stop();
    /// <summary>Real delivery cadence, ms (loopback: the poll cadence).</summary>
    double ActualPeriodMs { get; }
    EndpointTier Tier { get; }
    long DiscontinuityCount { get; }
    event CaptureDataHandler? DataAvailable;
    event Action<int>? Faulted;
}

// ============================================================================
// Legacy backend: NAudio adapters. Byte-identical behavior to the pre-abstraction
// engine (including the reflection read-back, which lives here now). Kept as the
// operator kill-switch (config engineBackend: "legacy") until the custom clients
// have field mileage.
// ============================================================================

internal sealed class NAudioRenderEndpoint : IRenderEndpoint
{
    private readonly MMDevice _device;
    private readonly int _requestedBufferMs;
    private readonly int _sampleRate;
    private WasapiOut? _out;

    public int ActualBufferMs { get; private set; }
    public double ActualPeriodMs { get; private set; }
    public EndpointTier Tier => EndpointTier.Legacy;
    public event Action<int>? Faulted { add { } remove { } } // NAudio surfaces no faults

    public NAudioRenderEndpoint(MMDevice device, int requestedBufferMs, int sampleRate)
    {
        _device = device;
        _requestedBufferMs = requestedBufferMs;
        _sampleRate = sampleRate > 0 ? sampleRate : 48000;
    }

    public void Init(ISampleProvider provider)
    {
        _out = new WasapiOut(_device, AudioClientShareMode.Shared, true, _requestedBufferMs);
        _out.Init(provider);

        // HONEST NUMBERS (moved verbatim from the old TryInitRender): Windows rounds
        // the request up to whole periods and NAudio keeps the buffer topped to FULL,
        // so real FIFO depth = real BufferSize + ~one period of mix-ahead. NAudio
        // exposes neither, hence the guarded reflection.
        ActualBufferMs = _requestedBufferMs;
        ActualPeriodMs = 10;
        try
        {
            var acField = typeof(WasapiOut).GetField("audioClient",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (acField?.GetValue(_out) is AudioClient ac)
            {
                int realBufferMs = (int)Math.Round(ac.BufferSize * 1000.0 / _sampleRate);
                int periodMs = (int)Math.Max(1, _device.AudioClient.DefaultDevicePeriod / 10000);
                if (realBufferMs > 0)
                {
                    ActualBufferMs = realBufferMs;
                    ActualPeriodMs = periodMs;
                }
            }
        }
        catch
        {
            // Reflection shape changed - keep the requested values as the estimate.
        }
    }

    public void Play() => _out?.Play();
    public void Stop() { try { _out?.Stop(); } catch { } }

    public void Dispose()
    {
        try { _out?.Dispose(); } catch { }
        _out = null;
    }
}

internal sealed class NAudioCaptureEndpoint : ICaptureEndpoint
{
    private readonly WasapiCapture _capture;
    private float[] _scratch = [];

    public double ActualPeriodMs => 10;
    public EndpointTier Tier => EndpointTier.Legacy;
    public long DiscontinuityCount => 0;
    public event CaptureDataHandler? DataAvailable;
    public event Action<int>? Faulted { add { } remove { } }

    public NAudioCaptureEndpoint(MMDevice device, bool isLoopback, int bufferMs,
        int sampleRate, int channels)
    {
        _capture = isLoopback
            ? new PolledLoopbackCapture(device)
            : new WasapiCapture(device, true, bufferMs);
        _capture.WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);

        _capture.DataAvailable += (_, e) =>
        {
            // NAudio's capture thread runs at NORMAL priority with no MMCSS.
            MmcssHelper.BoostCurrentThread();

            int floatCount = e.BytesRecorded / 4;
            int frames = channels > 0 ? floatCount / channels : 0;
            if (frames <= 0) return;

            if (_scratch.Length < floatCount)
            {
                _scratch = new float[Math.Max(floatCount, Math.Max(64, _scratch.Length * 2))];
            }
            Buffer.BlockCopy(e.Buffer, 0, _scratch, 0, e.BytesRecorded);
            DataAvailable?.Invoke(_scratch, frames);
        };
    }

    public void Start() => _capture.StartRecording();
    public void Stop() { try { _capture.StopRecording(); } catch { } }

    public void Dispose()
    {
        try { _capture.Dispose(); } catch { }
    }
}


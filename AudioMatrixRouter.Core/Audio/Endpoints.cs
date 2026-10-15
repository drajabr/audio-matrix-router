using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioMatrixRouter.Audio;

/// <summary>Interleaved float32 capture delivery. The buffer is only valid for the
/// duration of the callback (reused scratch).</summary>
public delegate void CaptureDataHandler(float[] interleaved, int frames);

/// <summary>Period tier an endpoint runs at (the fallback/degrade ladder).</summary>
public enum EndpointTier
{
    Exclusive,      // exclusive-mode event-driven stream at the device's minimum period
    MinPeriod,      // IAudioClient3 minimum shared period
    DoublePeriod,   // 2x minimum (first degrade rung)
    DefaultPeriod,  // classic default-period event stream
    EventLoopback,  // event-driven loopback kept awake by a silent render stream
    DefaultPolled,  // polled loopback at the default period
    Legacy,         // NAudio backend
}

/// <summary>
/// Inter-delivery gap histogram over a sliding ~10 s window (two 5 s halves). Records
/// on the audio thread; <see cref="WorstRecurringGapMs"/> is read from the health
/// tick. A gap must recur (default 3 times) to count, so one scheduling hiccup during
/// a device refresh cannot raise the latency floor for the whole session, while a
/// bursty producer (hundreds of 16 ms gaps per window) is seen within seconds.
/// </summary>
public sealed class CadenceTracker
{
    private const int Bins = 128;             // 1 ms bins, 0..127 ms
    private const double HalfWindowMs = 5000;
    private readonly int[] _current = new int[Bins];
    private readonly int[] _previous = new int[Bins];
    private readonly object _lock = new();
    private long _lastTicks;
    private long _halfStartTicks;
    private static readonly double s_ticksPerMs = System.Diagnostics.Stopwatch.Frequency / 1000.0;

    public void Record()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        lock (_lock)
        {
            if (_halfStartTicks == 0) _halfStartTicks = now;
            if (_lastTicks != 0)
            {
                double gapMs = (now - _lastTicks) / s_ticksPerMs;
                int bin = (int)Math.Ceiling(gapMs);
                if (bin < 0) bin = 0;
                if (bin >= Bins) bin = Bins - 1;
                _current[bin]++;
            }
            _lastTicks = now;
            if ((now - _halfStartTicks) / s_ticksPerMs >= HalfWindowMs)
            {
                Array.Copy(_current, _previous, Bins);
                Array.Clear(_current);
                _halfStartTicks = now;
            }
        }
    }

    /// <summary>Test hook: records a gap of the given length directly.</summary>
    public void RecordGapForTest(double gapMs)
    {
        lock (_lock)
        {
            int bin = Math.Clamp((int)Math.Ceiling(gapMs), 0, Bins - 1);
            _current[bin]++;
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            Array.Clear(_current);
            Array.Clear(_previous);
            _lastTicks = 0;
            _halfStartTicks = 0;
        }
    }

    /// <summary>Largest gap (ms) seen at least <paramref name="minCount"/> times in the window; 0 when none.</summary>
    public double WorstRecurringGapMs(int minCount = 3)
    {
        lock (_lock)
        {
            for (int bin = Bins - 1; bin > 0; bin--)
            {
                if (_current[bin] + _previous[bin] >= minCount) return bin;
            }
            return 0;
        }
    }
}

public interface IRenderEndpoint : IDisposable
{
    void Init(ISampleProvider provider);
    void Play();
    void Stop();
    /// <summary>Real allocated buffer (read back), ms. 0 = unknown.</summary>
    int ActualBufferMs { get; }
    /// <summary>Real engine period (read back), ms. 0 = unknown.</summary>
    double ActualPeriodMs { get; }
    /// <summary>FIFO depth the render loop keeps queued, ms (= the output latency the
    /// endpoint adds on top of its engine period). Settable live; endpoints that cannot
    /// control it (legacy) report their full buffer.</summary>
    int FifoMs { get; }
    void SetTargetPaddingMs(int paddingMs);
    EndpointTier Tier { get; }
    /// <summary>Mid-stream death (device invalidated / stalled). Fired at most once,
    /// from the audio thread - subscribers must marshal, never restart inline.</summary>
    event Action<int>? Faulted;
}

public interface ICaptureEndpoint : IDisposable
{
    void Start();
    void Stop();
    /// <summary>Nominal delivery cadence, ms (the stream period; loopback: the poll cadence).</summary>
    double ActualPeriodMs { get; }
    /// <summary>Measured: the worst inter-delivery gap that recurred (3+ times) over the
    /// last ~10 s, ms. 0 until enough deliveries exist. Virtual cables deliver in bursts
    /// well above their nominal period; the budget must use this, not the period.</summary>
    double MeasuredCadenceMs { get; }
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
    public int FifoMs => ActualBufferMs; // NAudio always tops its buffer to full
    public void SetTargetPaddingMs(int paddingMs) { }
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

    private readonly CadenceTracker _cadence = new();

    public double ActualPeriodMs => 10;
    public double MeasuredCadenceMs => _cadence.WorstRecurringGapMs();
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
            _cadence.Record();
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


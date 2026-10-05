using AudioMatrixRouter.Audio.Wasapi;
using NAudio.Wave;
using System.Threading;

namespace AudioMatrixRouter.Audio;

/// <summary>
/// High-performance render transport: an event-driven shared-mode WASAPI stream at the
/// driver's minimum period, serviced by a dedicated "Pro Audio" MMCSS thread.
///
/// Unlike NAudio's WasapiOut (which keeps its whole buffer topped up, so latency == buffer
/// length), this client holds exactly <see cref="TargetDepthFrames"/> frames ahead of the
/// engine's read cursor: two periods of protection plus the user's safety margin. Each
/// event it refills only what the engine consumed, so output latency is the depth, not
/// whatever buffer size the engine happened to allocate.
/// </summary>
public sealed class LowLatencyRenderClient : IDisposable
{
    private readonly string _endpointId;
    private WasapiSharedStream? _stream;
    private IAudioRenderClient? _renderClient;
    private ISampleProvider? _provider;
    private Thread? _thread;
    private volatile bool _stopRequested;
    private float[] _scratch = [];
    private long _underruns;

    public LowLatencyRenderClient(string endpointId)
    {
        _endpointId = endpointId;
    }

    public int SampleRate { get; private set; }
    public int Channels { get; private set; }
    public uint PeriodFrames { get; private set; }
    public uint BufferFrames { get; private set; }
    public uint TargetDepthFrames { get; private set; }
    public double PeriodMs => SampleRate > 0 ? PeriodFrames * 1000.0 / SampleRate : 0;
    public double DepthMs => SampleRate > 0 ? TargetDepthFrames * 1000.0 / SampleRate : 0;
    public bool LowLatencyPeriod { get; private set; }
    public string TransportLabel { get; private set; } = string.Empty;
    public long Underruns => Interlocked.Read(ref _underruns);

    /// <summary>
    /// Opens and initialises the stream. The margin is added on top of a two-period depth,
    /// capped by the buffer the engine allocated (any margin beyond that is carried by the
    /// ring buffers on the capture side instead).
    /// </summary>
    public bool Initialize(int marginMs)
    {
        _stream = WasapiSharedStream.Open(_endpointId);
        if (_stream == null) return false;

        int fallbackBufferMs = Math.Max(20, 20 + Math.Max(0, marginMs));
        if (!_stream.Initialize(loopback: false, fallbackBufferMs))
        {
            _stream.Dispose();
            _stream = null;
            return false;
        }

        _renderClient = _stream.GetService<IAudioRenderClient>(WasapiConstants.IidIAudioRenderClient);
        if (_renderClient == null)
        {
            _stream.Dispose();
            _stream = null;
            return false;
        }

        SampleRate = _stream.SampleRate;
        Channels = _stream.Channels;
        PeriodFrames = _stream.PeriodFrames;
        BufferFrames = _stream.BufferFrames;
        LowLatencyPeriod = _stream.LowLatencyPeriod;

        uint marginFrames = (uint)Math.Max(0, Math.Round(Math.Max(0, marginMs) / 1000.0 * SampleRate));
        uint desired = PeriodFrames * 2 + marginFrames;
        TargetDepthFrames = Math.Clamp(desired, Math.Min(PeriodFrames, BufferFrames), BufferFrames);
        TransportLabel = $"WASAPI {(LowLatencyPeriod ? "low-latency" : "shared")} {PeriodMs:0.##} ms period";
        return true;
    }

    public bool Start(ISampleProvider provider)
    {
        if (_stream == null || _renderClient == null) return false;
        if (provider.WaveFormat.SampleRate != SampleRate || provider.WaveFormat.Channels != Channels) return false;

        _provider = provider;
        _stopRequested = false;
        _thread = new Thread(RenderLoop)
        {
            IsBackground = true,
            Name = $"HpRender {_endpointId}",
            Priority = ThreadPriority.Highest,
        };
        _thread.Start();
        return true;
    }

    private void RenderLoop()
    {
        var stream = _stream;
        var render = _renderClient;
        var provider = _provider;
        if (stream == null || render == null || provider == null) return;

        var mmcss = Avrt.EnterProAudio();
        try
        {
            // Pre-roll exactly the target depth, then let the engine run.
            Fill(stream, render, provider, TargetDepthFrames);
            if (!stream.Start()) return;

            int waitMs = (int)Math.Clamp(Math.Ceiling(PeriodMs * 10), 20, 500);
            bool first = true;
            while (!_stopRequested)
            {
                if (!stream.Event.WaitOne(waitMs)) continue;
                if (_stopRequested) break;

                uint padding = stream.GetCurrentPadding();
                if (padding == 0 && !first)
                {
                    Interlocked.Increment(ref _underruns);
                }
                first = false;

                uint available = BufferFrames > padding ? BufferFrames - padding : 0;
                uint want = TargetDepthFrames > padding ? TargetDepthFrames - padding : 0;
                uint toWrite = Math.Min(want, available);
                if (toWrite > 0)
                {
                    Fill(stream, render, provider, toWrite);
                }
            }
        }
        catch
        {
            // Device invalidated or driver fault: fall out; the engine's device-refresh path restarts.
        }
        finally
        {
            try { stream.Stop(); } catch { }
            Avrt.Leave(mmcss);
        }
    }

    private void Fill(WasapiSharedStream stream, IAudioRenderClient render, ISampleProvider provider, uint frames)
    {
        if (frames == 0) return;
        int samples = (int)frames * Channels;
        if (_scratch.Length < samples)
        {
            _scratch = new float[Math.Max(samples, _scratch.Length * 2)];
        }

        int read = provider.Read(_scratch, 0, samples);
        if (read < samples)
        {
            Array.Clear(_scratch, read, samples - read);
        }

        int hr = render.GetBuffer(frames, out var data);
        if (hr < 0 || data == IntPtr.Zero) return;
        stream.Format.FromFloat(_scratch, data, (int)frames);
        render.ReleaseBuffer(frames, 0);
    }

    public void Stop()
    {
        _stopRequested = true;
        try { _stream?.Event.Set(); } catch { }
        var t = _thread;
        if (t != null && t.IsAlive && t != Thread.CurrentThread)
        {
            t.Join(1000);
        }
        _thread = null;
        try { _stream?.Stop(); } catch { }
    }

    public void Dispose()
    {
        Stop();
        _renderClient = null;
        _provider = null;
        _stream?.Dispose();
        _stream = null;
    }
}

/// <summary>Silence source used to keep a loopback endpoint's engine awake at a small period.</summary>
internal sealed class SilenceSampleProvider : ISampleProvider
{
    public SilenceSampleProvider(int sampleRate, int channels)
    {
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        Array.Clear(buffer, offset, count);
        return count;
    }
}

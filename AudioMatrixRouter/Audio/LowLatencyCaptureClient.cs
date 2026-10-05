using AudioMatrixRouter.Audio.Wasapi;
using System.Threading;

namespace AudioMatrixRouter.Audio;

/// <summary>
/// High-performance capture transport: event-driven shared-mode WASAPI at the driver's
/// minimum period on a "Pro Audio" MMCSS thread. Every event drains all pending packets
/// and hands them to the engine as interleaved floats.
///
/// Loopback endpoints are opened event-driven as well (NAudio polls them every ~50 ms).
/// Because a loopback stream only receives events while the endpoint's engine is running,
/// a silent low-latency render stream is kept open on the same endpoint: it both keeps the
/// engine awake and pulls the endpoint's period down to the minimum, so loopback packets
/// arrive at the same small cadence as hardware capture.
/// </summary>
public sealed class LowLatencyCaptureClient : IDisposable
{
    private readonly string _endpointId;
    private readonly bool _loopback;
    private WasapiSharedStream? _stream;
    private IAudioCaptureClient? _captureClient;
    private LowLatencyRenderClient? _keepAlive;
    private Action<float[], int>? _onFrames;
    private Thread? _thread;
    private volatile bool _stopRequested;
    private float[] _scratch = [];

    public LowLatencyCaptureClient(string endpointId, bool loopback)
    {
        _endpointId = endpointId;
        _loopback = loopback;
    }

    public int SampleRate { get; private set; }
    public int Channels { get; private set; }
    public uint PeriodFrames { get; private set; }
    public double PeriodMs => SampleRate > 0 ? PeriodFrames * 1000.0 / SampleRate : 0;
    public bool LowLatencyPeriod { get; private set; }
    public string TransportLabel { get; private set; } = string.Empty;

    public bool Initialize()
    {
        if (_loopback)
        {
            // Keep-alive first so the endpoint's engine period is already minimal when the
            // loopback stream is created and reports its period.
            _keepAlive = new LowLatencyRenderClient(_endpointId);
            if (!_keepAlive.Initialize(0))
            {
                _keepAlive.Dispose();
                _keepAlive = null;
            }
            else
            {
                _keepAlive.Start(new SilenceSampleProvider(_keepAlive.SampleRate, _keepAlive.Channels));
            }
        }

        _stream = WasapiSharedStream.Open(_endpointId);
        if (_stream == null) { Cleanup(); return false; }

        // Fallback buffer for the classic path is overflow headroom only (we drain every event).
        if (!_stream.Initialize(_loopback, fallbackBufferMs: 100))
        {
            Cleanup();
            return false;
        }

        _captureClient = _stream.GetService<IAudioCaptureClient>(WasapiConstants.IidIAudioCaptureClient);
        if (_captureClient == null) { Cleanup(); return false; }

        SampleRate = _stream.SampleRate;
        Channels = _stream.Channels;
        PeriodFrames = _stream.PeriodFrames;
        LowLatencyPeriod = _stream.LowLatencyPeriod;
        TransportLabel = _loopback
            ? $"WASAPI loopback (event-driven) {PeriodMs:0.##} ms period"
            : $"WASAPI {(LowLatencyPeriod ? "low-latency" : "shared")} {PeriodMs:0.##} ms period";
        return true;
    }

    public bool Start(Action<float[], int> onFrames)
    {
        if (_stream == null || _captureClient == null) return false;
        _onFrames = onFrames;
        _stopRequested = false;
        _thread = new Thread(CaptureLoop)
        {
            IsBackground = true,
            Name = $"HpCapture {_endpointId}",
            Priority = ThreadPriority.Highest,
        };
        _thread.Start();
        return true;
    }

    private void CaptureLoop()
    {
        var stream = _stream;
        var capture = _captureClient;
        var onFrames = _onFrames;
        if (stream == null || capture == null || onFrames == null) return;

        var mmcss = Avrt.EnterProAudio();
        try
        {
            if (!stream.Start()) return;
            int waitMs = (int)Math.Clamp(Math.Ceiling(PeriodMs * 10), 20, 500);

            while (!_stopRequested)
            {
                if (!stream.Event.WaitOne(waitMs))
                {
                    // Loopback streams go quiet when nothing renders; the keep-alive should
                    // prevent that, but poll defensively so a stalled event never wedges us.
                    if (!_loopback) continue;
                }
                if (_stopRequested) break;

                while (!_stopRequested)
                {
                    int hr = capture.GetNextPacketSize(out var packetFrames);
                    if (hr < 0 || packetFrames == 0) break;

                    hr = capture.GetBuffer(out var data, out var frames, out var flags, out _, out _);
                    if (hr < 0) break;
                    if (frames == 0)
                    {
                        capture.ReleaseBuffer(0);
                        break;
                    }

                    int samples = (int)frames * Channels;
                    if (_scratch.Length < samples)
                    {
                        _scratch = new float[Math.Max(samples, _scratch.Length * 2)];
                    }

                    if ((flags & WasapiConstants.BufferFlagsSilent) != 0 || data == IntPtr.Zero)
                    {
                        Array.Clear(_scratch, 0, samples);
                    }
                    else
                    {
                        stream.Format.ToFloat(data, _scratch, (int)frames);
                    }

                    capture.ReleaseBuffer(frames);
                    onFrames(_scratch, (int)frames);
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

    private void Cleanup()
    {
        _captureClient = null;
        _stream?.Dispose();
        _stream = null;
        _keepAlive?.Dispose();
        _keepAlive = null;
    }

    public void Dispose()
    {
        Stop();
        Cleanup();
        _onFrames = null;
    }
}

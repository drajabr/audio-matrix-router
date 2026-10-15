using System.Runtime.InteropServices;
using System.Threading;

namespace AudioMatrixRouter.Audio.Wasapi;

/// <summary>
/// Shared-mode WASAPI capture endpoint. Two modes:
///
///   EventMic      — event-driven; IAudioClient3 small periods when the driver
///                   supports them (same ladder as the render client), draining
///                   ALL packets per wake.
///   EventLoopback — event-driven loopback. A loopback tap only signals while the
///                   endpoint's engine runs, so a silent shared render stream is kept
///                   open on the same endpoint (the keep-alive) and topped up from this
///                   thread. Removes the polling race (gap tail 30-42 ms → the engine's
///                   own 10 ms). Falls back to PolledLoopback if the open fails.
///   PolledLoopback — the proven polled path (20 ms buffer, ~10 ms poll); fallback.
///
/// Exclusive mode (opt-in): EventMic tries an exclusive event-driven stream at the
/// device's minimum period first (3 ms on most HDA devices against 10 ms shared).
///
/// Delivery is interleaved float32 (converted from the mix encoding when needed) at
/// the endpoint's mix rate/channels via <see cref="DataAvailable"/> — buffer valid
/// only for the duration of the callback. Faults latch once; an idle loopback is
/// legal, so PolledLoopback faults on HRESULTs only, never on silence.
/// </summary>
internal sealed class WasapiCaptureClient : ICaptureEndpoint
{
    public enum CaptureMode { EventMic, EventLoopback, PolledLoopback }

    private readonly string _endpointId;
    private CaptureMode _mode;
    private readonly EndpointTier _requestedTier;
    private readonly string _label;
    private readonly bool _exclusiveWanted;
    private readonly CadenceTracker _cadence = new();

    // Keep-alive render stream for EventLoopback (owned by the capture thread).
    private IMMDeviceEnumerator? _kaEnumerator;
    private IMMDevice? _kaDevice;
    private IAudioClient? _kaClient;
    private IAudioRenderClient? _kaRender;
    private uint _kaFrames;

    // Exclusive rung bookkeeping (see TryLadderInit).
    private int _exclusiveAttempts;
    private long _exclusiveAlignedPeriod;
    private bool _exclusiveRetryPending;

    /// <summary>Why the exclusive rung was not taken (empty when it was, or was not wanted).</summary>
    public string ExclusiveRefusal { get; private set; } = "";

    private readonly AutoResetEvent _frameEvent = new(false);
    private readonly ManualResetEventSlim _stopRequest = new(false);
    private readonly ManualResetEventSlim _initDone = new(false);
    private readonly object _stateLock = new();

    private Thread? _thread;
    private volatile bool _disposed;
    private int _faultLatch;
    private bool _initOk;
    private long _discontinuities;

    public double ActualPeriodMs { get; private set; } = 10;
    public double MeasuredCadenceMs => _cadence.WorstRecurringGapMs();
    public int ActualSampleRate { get; private set; }
    public int ActualChannels { get; private set; }
    public int ActualBufferMs { get; private set; }
    public EndpointTier Tier { get; private set; } = EndpointTier.DefaultPeriod;
    public long DiscontinuityCount => Interlocked.Read(ref _discontinuities);

    public event CaptureDataHandler? DataAvailable;
    public event Action<int>? Faulted;

    public WasapiCaptureClient(string endpointId, CaptureMode mode, EndpointTier requestedTier, string label,
        bool exclusiveWanted = false)
    {
        _endpointId = endpointId;
        _mode = mode;
        _requestedTier = requestedTier;
        _label = label;
        _exclusiveWanted = exclusiveWanted && mode == CaptureMode.EventMic;
    }

    /// <summary>Starts the audio thread, which activates + initializes + begins
    /// capturing. Blocks until init resolves; throws on failure (caller treats it
    /// like today's StartRecording failure).</summary>
    public void Start()
    {
        _thread = new Thread(AudioThread)
        {
            IsBackground = true,
            Name = "wasapi-capture:" + _label,
        };
        _thread.Start();
        // Bounded (see WasapiRenderClient.Init): a wedged driver fails THIS device
        // instead of blocking the engine start.
        if (!_initDone.Wait(InitTimeoutMs))
        {
            Stop();
            throw new TimeoutException($"WASAPI capture init timed out for {_label}");
        }
        if (!_initOk)
        {
            throw new InvalidOperationException($"WASAPI capture init failed for {_label}");
        }
    }

    private const int InitTimeoutMs = 5000;
    private const int DisposeJoinMs = 500;

    public void Stop()
    {
        _stopRequest.Set();
        try { _frameEvent.Set(); } catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Stop();
        if (_thread is { } t && t != Thread.CurrentThread)
        {
            if (!t.Join(DisposeJoinMs))
            {
                return; // leak COM state deliberately rather than release under a live thread
            }
        }
        _frameEvent.Dispose();
        _stopRequest.Dispose();
        _initDone.Dispose();
    }

    // ===================================================================== thread

    private void AudioThread()
    {
        bool comInit = WasapiActivation.EnterComThread();
        MmcssHelper.BoostCurrentThread();

        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioClient? client = null;
        IAudioCaptureClient? captureService = null;
        SampleConverter? converter = null;
        float[] scratch = [];

        try
        {
            if (!TryLadderInit(ref enumerator, ref device, ref client,
                    out captureService, out converter))
            {
                _initOk = false;
                _initDone.Set();
                return;
            }

            int hr = client!.Start();
            if (hr != WasapiConstants.S_OK)
            {
                _initOk = false;
                _initDone.Set();
                return;
            }

            _initOk = true;
            _initDone.Set();

            int channels = ActualChannels;
            bool polled = _mode == CaptureMode.PolledLoopback;
            int pollMs = Math.Max(2, ActualBufferMs / 2);
            int timeoutMs = Math.Max(500, (int)Math.Ceiling(ActualPeriodMs * 40));
            var waits = new WaitHandle[] { _frameEvent, _stopRequest.WaitHandle };

            while (!_stopRequest.IsSet)
            {
                if (polled)
                {
                    if (_stopRequest.Wait(pollMs)) break;
                }
                else
                {
                    int signaled = WaitHandle.WaitAny(waits, timeoutMs);
                    if (signaled == 1) break;
                    if (signaled == WaitHandle.WaitTimeout)
                    {
                        // No event for 40 periods. Probe instead of assuming death
                        // (a false fault restarts the whole engine): a packet-size
                        // query on a dead endpoint returns DEVICE_INVALIDATED, which
                        // the drain loop below turns into a real fault; S_OK means
                        // the endpoint is alive and merely quiet.
                    }
                }

                if (_kaRender is not null) TopUpKeepAlive();

                // Drain EVERYTHING available this wake.
                while (!_stopRequest.IsSet)
                {
                    int phr = captureService!.GetNextPacketSize(out var packetFrames);
                    if (IsFatal(phr)) { Fault(phr); goto done; }
                    if (phr != WasapiConstants.S_OK || packetFrames == 0) break;

                    phr = captureService.GetBuffer(out var dataPtr, out var frames,
                        out var flags, out _, out _);
                    if (IsFatal(phr)) { Fault(phr); goto done; }
                    if (phr != WasapiConstants.S_OK) break;

                    if (frames > 0)
                    {
                        int samples = (int)frames * channels;
                        if (scratch.Length < samples)
                        {
                            scratch = new float[Math.Max(samples, Math.Max(256, scratch.Length * 2))];
                        }

                        if ((flags & WasapiConstants.BUFFERFLAGS_SILENT) != 0)
                        {
                            Array.Clear(scratch, 0, samples);
                        }
                        else
                        {
                            converter!.ReadFromDevice(dataPtr, scratch, samples);
                        }
                        if ((flags & WasapiConstants.BUFFERFLAGS_DATA_DISCONTINUITY) != 0)
                        {
                            Interlocked.Increment(ref _discontinuities);
                        }

                        _cadence.Record();
                        try { DataAvailable?.Invoke(scratch, (int)frames); }
                        catch { /* a consumer bug must not kill the capture loop */ }
                    }

                    phr = captureService.ReleaseBuffer(frames);
                    if (IsFatal(phr)) { Fault(phr); goto done; }
                    if (phr != WasapiConstants.S_OK) break;
                }
            }

            done:
            try { client.Stop(); } catch { }
            try { client.Reset(); } catch { }
        }
        catch
        {
            if (!_initDone.IsSet) { _initOk = false; _initDone.Set(); }
            Fault(WasapiConstants.AUDCLNT_E_DEVICE_INVALIDATED);
        }
        finally
        {
            // Leaving without a Stop request = the stream broke in a way nothing above
            // latched; the engine must hear about it or this input stays silent forever.
            if (_initOk && !_stopRequest.IsSet) Fault(WasapiConstants.AUDCLNT_E_STALLED);
            try { if (captureService is not null) Marshal.ReleaseComObject(captureService); } catch { }
            WasapiActivation.ReleaseActivation(enumerator, device, client);
            StopKeepAlive();
            WasapiActivation.ExitComThread(comInit);
        }
    }

    // ----- keep-alive (EventLoopback) -----

    private bool TryStartKeepAlive(IntPtr mixFmt)
    {
        StopKeepAlive();
        int hr = WasapiActivation.TryActivate(_endpointId, out _kaEnumerator, out _kaDevice, out _kaClient, out _);
        if (hr != WasapiConstants.S_OK || _kaClient is null) { StopKeepAlive(); return false; }
        // Push-mode shared stream, 200 ms deep: the engine keeps processing (and the
        // loopback tap keeps signalling) while a started stream exists, silent or not.
        hr = _kaClient.Initialize(WasapiConstants.AUDCLNT_SHAREMODE_SHARED, 0, 200 * 10_000L, 0, mixFmt, IntPtr.Zero);
        if (hr != WasapiConstants.S_OK) { StopKeepAlive(); return false; }
        if (_kaClient.GetBufferSize(out _kaFrames) != WasapiConstants.S_OK || _kaFrames == 0) { StopKeepAlive(); return false; }
        var iid = WasapiConstants.IID_IAudioRenderClient;
        if (_kaClient.GetService(ref iid, out var svc) != WasapiConstants.S_OK || svc is null) { StopKeepAlive(); return false; }
        _kaRender = (IAudioRenderClient)svc;
        if (_kaRender.GetBuffer(_kaFrames, out _) == WasapiConstants.S_OK)
        {
            _kaRender.ReleaseBuffer(_kaFrames, WasapiConstants.BUFFERFLAGS_SILENT);
        }
        if (_kaClient.Start() != WasapiConstants.S_OK) { StopKeepAlive(); return false; }
        return true;
    }

    private void TopUpKeepAlive()
    {
        var client = _kaClient;
        var render = _kaRender;
        if (client is null || render is null) return;
        if (client.GetCurrentPadding(out var padding) != WasapiConstants.S_OK) return;
        uint free = _kaFrames > padding ? _kaFrames - padding : 0;
        if (free == 0) return;
        if (render.GetBuffer(free, out _) == WasapiConstants.S_OK)
        {
            render.ReleaseBuffer(free, WasapiConstants.BUFFERFLAGS_SILENT);
        }
    }

    private void StopKeepAlive()
    {
        try { _kaClient?.Stop(); } catch { }
        try { if (_kaRender is not null) Marshal.ReleaseComObject(_kaRender); } catch { }
        _kaRender = null;
        WasapiActivation.ReleaseActivation(_kaEnumerator, _kaDevice, _kaClient);
        _kaEnumerator = null;
        _kaDevice = null;
        _kaClient = null;
        _kaFrames = 0;
    }

    private bool TryLadderInit(ref IMMDeviceEnumerator? enumerator, ref IMMDevice? device,
        ref IAudioClient? client, out IAudioCaptureClient? captureService,
        out SampleConverter? converter)
    {
        captureService = null;
        converter = null;

        for (int attempt = 0; ; attempt++)
        {
            WasapiActivation.ReleaseActivation(enumerator, device, client);
            enumerator = null; device = null; client = null;

            int hr = WasapiActivation.TryActivate(_endpointId,
                out enumerator, out device, out client, out var client3);
            if (hr != WasapiConstants.S_OK || client is null) return false;

            IntPtr mixFmt = IntPtr.Zero;
            try
            {
                if (client.GetMixFormat(out mixFmt) != WasapiConstants.S_OK || mixFmt == IntPtr.Zero)
                    return false;
                var info = WasapiFormat.Parse(mixFmt);
                if (info.SampleRate <= 0 || info.Channels <= 0) return false;

                if (_mode == CaptureMode.EventLoopback)
                {
                    if (TryStartKeepAlive(mixFmt))
                    {
                        hr = client.Initialize(WasapiConstants.AUDCLNT_SHAREMODE_SHARED,
                            WasapiConstants.STREAMFLAGS_EVENTCALLBACK | WasapiConstants.STREAMFLAGS_LOOPBACK,
                            40 * 10_000L, 0, mixFmt, IntPtr.Zero);
                        if (hr == WasapiConstants.S_OK)
                        {
                            Tier = EndpointTier.EventLoopback;
                            ActualPeriodMs = client.GetDevicePeriod(out var lbPeriod, out _) == WasapiConstants.S_OK
                                             && lbPeriod > 0 ? lbPeriod / 10_000.0 : 10;
                            return FinishInit(client, mixFmt, info, ref captureService, ref converter,
                                setEvent: true);
                        }
                        StopKeepAlive();
                    }
                    WasapiDiagnostics.Log($"[WASAPI] in {_label}: event-driven loopback unavailable (0x{hr:X8}) — polled fallback");
                    _mode = CaptureMode.PolledLoopback;
                    continue; // fresh client for the polled rung
                }

                if (_mode == CaptureMode.PolledLoopback)
                {
                    // 20 ms buffer → poll every ~10 ms; no events for loopback taps.
                    hr = client.Initialize(WasapiConstants.AUDCLNT_SHAREMODE_SHARED,
                        WasapiConstants.STREAMFLAGS_LOOPBACK, 20 * 10_000L, 0, mixFmt, IntPtr.Zero);
                    if (hr != WasapiConstants.S_OK) return false;
                    Tier = EndpointTier.DefaultPolled;
                    ActualPeriodMs = 10;
                    return FinishInit(client, mixFmt, info, ref captureService, ref converter,
                        setEvent: false);
                }

                // ---- C0: exclusive event-driven stream at the device minimum period ----
                if (_exclusiveWanted && (_exclusiveAttempts == 0 || _exclusiveRetryPending) && _exclusiveAttempts < 2)
                {
                    _exclusiveAttempts++;
                    bool retry = _exclusiveRetryPending;
                    _exclusiveRetryPending = false;
                    hr = WasapiExclusive.TryInitialize(client, info.SampleRate, info.Channels,
                        retry ? _exclusiveAlignedPeriod : 0, out var exFmt, out var exInfo, out var usedPeriod, out var fmtLabel);
                    if (hr == WasapiConstants.S_OK)
                    {
                        Tier = EndpointTier.Exclusive;
                        ActualPeriodMs = usedPeriod / 10_000.0;
                        bool ok;
                        try
                        {
                            ok = FinishInit(client, exFmt, exInfo, ref captureService, ref converter, setEvent: true);
                        }
                        finally
                        {
                            Marshal.FreeCoTaskMem(exFmt);
                        }
                        if (ok)
                        {
                            WasapiDiagnostics.Log($"[WASAPI] in {_label}: EXCLUSIVE {fmtLabel} period {ActualPeriodMs:0.##} ms");
                            return true;
                        }
                        ExclusiveRefusal = "exclusive stream opened but could not be serviced";
                        continue;
                    }
                    if (hr == WasapiConstants.AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED && !retry)
                    {
                        _exclusiveAlignedPeriod = WasapiExclusive.AlignedPeriod100ns(client, info.SampleRate);
                        if (_exclusiveAlignedPeriod > 0) { _exclusiveRetryPending = true; continue; }
                    }
                    ExclusiveRefusal = WasapiConstants.DescribeExclusiveFailure(hr);
                    WasapiDiagnostics.Log($"[WASAPI] in {_label}: exclusive rung skipped — {ExclusiveRefusal}");
                    continue; // shared ladder on a fresh client
                }
                int sharedAttempt = attempt - _exclusiveAttempts;

                bool smallPeriodWanted = _requestedTier is EndpointTier.MinPeriod or EndpointTier.DoublePeriod
                                         && client3 is not null
                                         && info.Encoding != MixEncoding.Other
                                         && sharedAttempt == 0;
                if (smallPeriodWanted)
                {
                    hr = client3!.GetSharedModeEnginePeriod(mixFmt,
                        out var defFrames, out var fundamental, out var minFrames, out var maxFrames);
                    if (hr == WasapiConstants.S_OK && minFrames > 0 && minFrames < defFrames)
                    {
                        uint target = _requestedTier == EndpointTier.DoublePeriod
                            ? Math.Min(maxFrames, minFrames * 2)
                            : minFrames;
                        if (fundamental > 0 && target > minFrames)
                        {
                            uint k = (target - minFrames + fundamental - 1) / fundamental;
                            target = Math.Min(maxFrames, minFrames + k * fundamental);
                        }

                        hr = client3.InitializeSharedAudioStream(
                            WasapiConstants.STREAMFLAGS_EVENTCALLBACK, target, mixFmt, IntPtr.Zero);
                        if (hr == WasapiConstants.S_OK)
                        {
                            Tier = _requestedTier;
                            ActualPeriodMs = target * 1000.0 / info.SampleRate;
                            return FinishInit(client, mixFmt, info, ref captureService,
                                ref converter, setEvent: true);
                        }
                        continue; // any failure → default rung on a fresh client
                    }
                }

                // Default-period event stream; buffer deep enough that a late thread
                // never loses a packet (depth is not latency on the capture side).
                hr = client.Initialize(WasapiConstants.AUDCLNT_SHAREMODE_SHARED,
                    WasapiConstants.STREAMFLAGS_EVENTCALLBACK, 40 * 10_000L, 0, mixFmt, IntPtr.Zero);
                if (hr == WasapiConstants.S_OK)
                {
                    Tier = EndpointTier.DefaultPeriod;
                    ActualPeriodMs = client.GetDevicePeriod(out var defPeriod, out _) == WasapiConstants.S_OK
                                     && defPeriod > 0 ? defPeriod / 10_000.0 : 10;
                    return FinishInit(client, mixFmt, info, ref captureService, ref converter,
                        setEvent: true);
                }

                if (sharedAttempt >= 2) return false;
            }
            finally
            {
                if (mixFmt != IntPtr.Zero) Marshal.FreeCoTaskMem(mixFmt);
            }
        }
    }

    private bool FinishInit(IAudioClient client, IntPtr fmt, WasapiFormatInfo info,
        ref IAudioCaptureClient? captureService, ref SampleConverter? converter, bool setEvent)
    {
        if (setEvent &&
            client.SetEventHandle(_frameEvent.SafeWaitHandle.DangerousGetHandle())
                != WasapiConstants.S_OK) return false;

        if (client.GetBufferSize(out var bufferFrames) == WasapiConstants.S_OK && bufferFrames > 0)
        {
            ActualBufferMs = (int)Math.Round(bufferFrames * 1000.0 / info.SampleRate);
        }

        var iid = WasapiConstants.IID_IAudioCaptureClient;
        if (client.GetService(ref iid, out var svc) != WasapiConstants.S_OK || svc is null)
            return false;
        captureService = (IAudioCaptureClient)svc;
        converter = new SampleConverter(info.Encoding);
        ActualSampleRate = info.SampleRate;
        ActualChannels = info.Channels;
        return true;
    }

    private static bool IsFatal(int hr) =>
        hr == WasapiConstants.AUDCLNT_E_DEVICE_INVALIDATED ||
        hr == WasapiConstants.AUDCLNT_E_RESOURCES_INVALIDATED;

    private void Fault(int hr)
    {
        if (_mode is CaptureMode.PolledLoopback or CaptureMode.EventLoopback && hr == 0) return; // idle loopback is legal
        if (Interlocked.Exchange(ref _faultLatch, 1) != 0) return;
        try { Faulted?.Invoke(hr); } catch { }
    }
}

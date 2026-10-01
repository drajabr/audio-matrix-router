using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.Wave;

namespace AudioMatrixRouter.Audio.Wasapi;

/// <summary>
/// Event-driven shared-mode WASAPI render endpoint with IAudioClient3 small-period
/// support and a per-device fallback ladder:
///
///   R1 InitializeSharedAudioStream at the tier's period (min or 2x min, quantized
///      to min + k*fundamental)
///   R2 on ENGINE_PERIODICITY_LOCKED: retry ONCE at the period another app locked
///   R3 classic Initialize at the default period; on UNSUPPORTED_FORMAT retry with
///      our float format + AUTOCONVERTPCM
///   R4 give up -> Init returns false, engine skips the device
///
/// A failed Initialize poisons the IAudioClient instance, so every rung re-activates
/// a fresh client. ALL COM work happens on the client's own MTA audio thread (Init
/// blocks on a completion event), which makes apartment questions moot.
///
/// Faults (device invalidated / stalled stream) are latched and surfaced once via
/// <see cref="Faulted"/>, from the audio thread — subscribers must marshal.
/// </summary>
internal sealed class WasapiRenderClient : IRenderEndpoint
{
    private readonly string _endpointId;
    private readonly int _requestedBufferMs;
    private readonly EndpointTier _requestedTier;
    private readonly string _label;

    private readonly AutoResetEvent _frameEvent = new(false);
    private readonly ManualResetEventSlim _stopRequest = new(false);
    private readonly ManualResetEventSlim _initDone = new(false);
    private readonly ManualResetEventSlim _playRequest = new(false);
    private readonly object _stateLock = new();

    private Thread? _thread;
    private ISampleProvider? _provider;
    private volatile bool _disposed;
    private int _faultLatch;
    private bool _initOk;

    public int ActualBufferMs { get; private set; }
    public double ActualPeriodMs { get; private set; }
    public int ActualSampleRate { get; private set; }
    public int ActualChannels { get; private set; }
    public EndpointTier Tier { get; private set; } = EndpointTier.DefaultPeriod;
    public long ProviderErrorCount => Interlocked.Read(ref _providerErrors);
    private long _providerErrors;

    // FIFO depth to keep queued, in device frames. The old loop topped the buffer up
    // to FULL on every event, so the output latency was the whole allocation (~22 ms
    // for a 10 ms request) whatever the knob said. Now each event writes only up to
    // this target; 0 = "full buffer" until the engine sets it.
    private volatile int _targetPaddingFrames;
    private uint _bufferFrames;

    public int FifoMs
    {
        get
        {
            int t = _targetPaddingFrames;
            if (t <= 0 || ActualSampleRate <= 0) return ActualBufferMs;
            return (int)Math.Round(t * 1000.0 / ActualSampleRate);
        }
    }

    public void SetTargetPaddingMs(int paddingMs)
    {
        if (ActualSampleRate <= 0) return;
        int frames = (int)Math.Round(paddingMs * ActualSampleRate / 1000.0);
        uint buffer = _bufferFrames;
        if (buffer > 0) frames = (int)Math.Min(frames, buffer);
        _targetPaddingFrames = Math.Max(1, frames);
    }

    public event Action<int>? Faulted;

    public WasapiRenderClient(string endpointId, int requestedBufferMs, EndpointTier requestedTier, string label)
    {
        _endpointId = endpointId;
        _requestedBufferMs = Math.Clamp(requestedBufferMs, 2, 500);
        _requestedTier = requestedTier;
        _label = label;
    }

    /// <summary>Spins up the audio thread, performs activation + the init ladder ON
    /// that thread, and blocks until it finishes. Throws on failure so the engine's
    /// existing TryInitRender catch treats it exactly like a NAudio init failure.</summary>
    public void Init(ISampleProvider provider)
    {
        _provider = provider;
        _thread = new Thread(AudioThread)
        {
            IsBackground = true,
            Name = "wasapi-render:" + _label,
        };
        _thread.Start();
        // Bounded: an NVIDIA HDMI endpoint mid display-handshake can sit inside
        // Activate/Initialize for many seconds. Give up on THIS device (the thread
        // finishes on its own and exits because Stop() was requested) instead of
        // holding the whole engine start hostage.
        if (!_initDone.Wait(InitTimeoutMs))
        {
            Stop();
            throw new TimeoutException($"WASAPI render init timed out for {_label}");
        }
        if (!_initOk)
        {
            throw new InvalidOperationException($"WASAPI render init failed for {_label}");
        }
    }

    private const int InitTimeoutMs = 5000;
    private const int DisposeJoinMs = 500;
    // A consuming shared-mode stream frees at least one period every period. No free
    // space for this long while the stream answers S_OK means the engine side stopped
    // pulling (display asleep, driver wedged) — a stall, which must surface as a fault
    // so the engine can promote another master and stop feeding a dead reference.
    private const double StallMs = 1000.0;

    public void Play() => _playRequest.Set();

    public void Stop()
    {
        _stopRequest.Set();
        _playRequest.Set(); // release a thread still waiting for Play
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
        // Never Join our own thread (a Faulted handler could re-enter Dispose from it).
        if (_thread is { } t && t != Thread.CurrentThread)
        {
            if (!t.Join(DisposeJoinMs))
            {
                // Driver wedged inside a WASAPI call: deliberately leak the COM state
                // (the thread owns it) rather than risk a use-after-free release.
                return;
            }
        }
        _frameEvent.Dispose();
        _stopRequest.Dispose();
        _playRequest.Dispose();
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
        IAudioRenderClient? renderService = null;
        SampleConverter? converter = null;
        float[] scratch = [];
        uint bufferFrames = 0;

        try
        {
            if (!TryLadderInit(ref enumerator, ref device, ref client, out var client3,
                    out renderService, out converter, out bufferFrames))
            {
                _initOk = false;
                _initDone.Set();
                return;
            }

            _bufferFrames = bufferFrames;
            _initOk = true;
            _initDone.Set();

            // Wait for Play (engine starts all outputs together after captures). The
            // engine sets the padding target in between, once it knows every output's
            // real buffer and period.
            _playRequest.Wait();
            if (_stopRequest.IsSet) return;

            // Pre-roll: queue TARGET frames of silence so the FIFO depth is deterministic
            // at t0 and the first period can never glitch. (Not the whole buffer: that
            // pinned the output latency at the full allocation.)
            uint preroll = TargetFrames(bufferFrames);
            if (renderService!.GetBuffer(preroll, out _) == WasapiConstants.S_OK)
            {
                renderService.ReleaseBuffer(preroll, WasapiConstants.BUFFERFLAGS_SILENT);
            }

            int hr = client!.Start();
            if (hr != WasapiConstants.S_OK)
            {
                Fault(hr);
                return;
            }

            int channels = ActualChannels;
            int timeoutMs = Math.Max(250, (int)Math.Ceiling(ActualPeriodMs * 20));
            var waits = new WaitHandle[] { _frameEvent, _stopRequest.WaitHandle };
            int consecutiveBufferErrors = 0;
            long lastProgressTicks = Stopwatch.GetTimestamp();
            double ticksPerMs = Stopwatch.Frequency / 1000.0;
            uint lastPadding = uint.MaxValue;

            while (!_stopRequest.IsSet)
            {
                int signaled = WaitHandle.WaitAny(waits, timeoutMs);
                if (signaled == 1) break;              // stop
                if (signaled == WaitHandle.WaitTimeout)
                {
                    // No event for 20 periods. Some drivers legally pause the event
                    // pump — do NOT assume death from silence alone (a false fault
                    // causes an engine restart LOOP). Probe instead: a padding query
                    // on a dead stream returns DEVICE_INVALIDATED, which faults below;
                    // a stream that answers S_OK but never frees space is handled by
                    // the progress check under it.
                }

                hr = client.GetCurrentPadding(out var padding);
                if (IsFatal(hr)) { Fault(hr); break; }
                if (hr != WasapiConstants.S_OK) continue;

                // Progress = the engine consumed something since we last looked. A
                // consuming shared-mode stream lowers padding every period; one that
                // answers S_OK but never does is stalled (display asleep, driver wedged).
                if (padding != lastPadding)
                {
                    lastPadding = padding;
                    lastProgressTicks = Stopwatch.GetTimestamp();
                }
                else if ((Stopwatch.GetTimestamp() - lastProgressTicks) / ticksPerMs > StallMs)
                {
                    WasapiDiagnostics.Log($"[WASAPI] render {_label}: no buffer progress for {StallMs:0} ms — stalled");
                    Fault(WasapiConstants.AUDCLNT_E_STALLED);
                    break;
                }

                // Top up to the padding TARGET, never to the whole buffer.
                uint free = bufferFrames - padding;
                uint target = TargetFrames(bufferFrames);
                uint frames = padding >= target ? 0 : Math.Min(free, target - padding);
                if (frames == 0) continue;
                lastPadding = padding + frames; // what GetCurrentPadding reports next if nothing was consumed

                hr = renderService.GetBuffer(frames, out var dataPtr);
                if (hr == WasapiConstants.AUDCLNT_E_BUFFER_ERROR)
                {
                    if (++consecutiveBufferErrors >= 3) { Fault(hr); break; }
                    continue;
                }
                if (IsFatal(hr) || hr != WasapiConstants.S_OK)
                {
                    if (IsFatal(hr)) { Fault(hr); break; }
                    continue;
                }
                consecutiveBufferErrors = 0;

                int samples = (int)frames * channels;
                if (scratch.Length < samples)
                {
                    scratch = new float[Math.Max(samples, Math.Max(256, scratch.Length * 2))];
                }

                bool silent = false;
                try
                {
                    int read = _provider!.Read(scratch, 0, samples);
                    if (read < samples)
                    {
                        Array.Clear(scratch, read, samples - read);
                    }
                }
                catch
                {
                    // A mixer bug must never kill the stream: emit silence, count it.
                    Interlocked.Increment(ref _providerErrors);
                    silent = true;
                }

                if (!silent)
                {
                    converter!.WriteToDevice(scratch, samples, dataPtr);
                }
                hr = renderService.ReleaseBuffer(frames,
                    silent ? WasapiConstants.BUFFERFLAGS_SILENT : 0u);
                if (IsFatal(hr)) { Fault(hr); break; }
            }

            try { client.Stop(); } catch { }
            try { client.Reset(); } catch { }

            // Leaving the loop without a Stop request means the stream broke in a way
            // that was not latched above — the engine must know, or this output goes
            // silent forever while everything else looks fine.
            if (!_stopRequest.IsSet) Fault(WasapiConstants.AUDCLNT_E_STALLED);
        }
        catch (Exception ex)
        {
            WasapiDiagnostics.Log($"[WASAPI] render {_label}: thread exception {ex.GetType().Name}: {ex.Message}");
            if (!_initDone.IsSet) { _initOk = false; _initDone.Set(); }
            Fault(WasapiConstants.AUDCLNT_E_DEVICE_INVALIDATED);
        }
        finally
        {
            try { if (renderService is not null) Marshal.ReleaseComObject(renderService); } catch { }
            WasapiActivation.ReleaseActivation(enumerator, device, client);
            WasapiActivation.ExitComThread(comInit);
        }
    }

    private bool TryLadderInit(ref IMMDeviceEnumerator? enumerator, ref IMMDevice? device,
        ref IAudioClient? client, out IAudioClient3? client3,
        out IAudioRenderClient? renderService, out SampleConverter? converter,
        out uint bufferFrames)
    {
        client3 = null;
        renderService = null;
        converter = null;
        bufferFrames = 0;

        for (int attempt = 0; ; attempt++)
        {
            // Fresh activation per rung: a failed Initialize poisons the instance.
            WasapiActivation.ReleaseActivation(enumerator, device, client);
            enumerator = null; device = null; client = null; client3 = null;

            int hr = WasapiActivation.TryActivate(_endpointId,
                out enumerator, out device, out client, out client3);
            if (hr != WasapiConstants.S_OK || client is null) return false;

            IntPtr mixFmt = IntPtr.Zero;
            IntPtr ownFmt = IntPtr.Zero;
            try
            {
                if (client.GetMixFormat(out mixFmt) != WasapiConstants.S_OK || mixFmt == IntPtr.Zero)
                    return false;
                var info = WasapiFormat.Parse(mixFmt);
                if (info.SampleRate <= 0 || info.Channels <= 0) return false;

                bool smallPeriodWanted = _requestedTier is EndpointTier.MinPeriod or EndpointTier.DoublePeriod
                                         && client3 is not null
                                         && info.Encoding != MixEncoding.Other
                                         && attempt == 0;

                if (smallPeriodWanted)
                {
                    hr = client3!.GetSharedModeEnginePeriod(mixFmt,
                        out var defFrames, out var fundamental, out var minFrames, out var maxFrames);
                    if (hr == WasapiConstants.S_OK && minFrames > 0 && minFrames < defFrames)
                    {
                        uint target = _requestedTier == EndpointTier.DoublePeriod
                            ? Math.Min(maxFrames, minFrames * 2)
                            : minFrames;
                        // quantize to min + k*fundamental
                        if (fundamental > 0 && target > minFrames)
                        {
                            uint k = (target - minFrames + fundamental - 1) / fundamental;
                            target = Math.Min(maxFrames, minFrames + k * fundamental);
                        }

                        hr = client3.InitializeSharedAudioStream(
                            WasapiConstants.STREAMFLAGS_EVENTCALLBACK, target, mixFmt, IntPtr.Zero);
                        if (hr == WasapiConstants.AUDCLNT_E_ENGINE_PERIODICITY_LOCKED)
                        {
                            // Another app pinned the engine period; match it (still better
                            // than default). Needs a fresh client — loop with attempt=1
                            // marked so we take the locked-period branch below.
                            _lockedPeriodRetry = true;
                            continue;
                        }
                        if (hr == WasapiConstants.S_OK)
                        {
                            Tier = _requestedTier;
                            return FinishInit(client, client3, mixFmt, info,
                                ref renderService, ref converter, ref bufferFrames);
                        }
                        // E_INVALIDARG / INVALID_DEVICE_PERIOD / FORMAT_LOCKED / other:
                        // fall through to the default-period rung on a fresh client.
                        continue;
                    }
                    // No real small-period support (min == default): default rung.
                }

                if (_lockedPeriodRetry && client3 is not null)
                {
                    _lockedPeriodRetry = false;
                    IntPtr curFmt = IntPtr.Zero;
                    try
                    {
                        if (client3.GetCurrentSharedModeEnginePeriod(out curFmt, out var lockedFrames)
                                == WasapiConstants.S_OK && lockedFrames > 0 &&
                            client3.InitializeSharedAudioStream(
                                WasapiConstants.STREAMFLAGS_EVENTCALLBACK, lockedFrames,
                                curFmt, IntPtr.Zero) == WasapiConstants.S_OK)
                        {
                            var lockedInfo = WasapiFormat.Parse(curFmt);
                            Tier = EndpointTier.DefaultPeriod;
                            return FinishInit(client, client3, curFmt, lockedInfo,
                                ref renderService, ref converter, ref bufferFrames);
                        }
                    }
                    finally
                    {
                        if (curFmt != IntPtr.Zero) Marshal.FreeCoTaskMem(curFmt);
                    }
                    continue; // locked retry failed → default rung on a fresh client
                }

                // ---- R3: classic default-period event stream ----
                long bufferDuration = Math.Max(1, _requestedBufferMs) * 10_000L;
                hr = client.Initialize(WasapiConstants.AUDCLNT_SHAREMODE_SHARED,
                    WasapiConstants.STREAMFLAGS_EVENTCALLBACK, bufferDuration, 0, mixFmt, IntPtr.Zero);
                if (hr == WasapiConstants.S_OK)
                {
                    Tier = EndpointTier.DefaultPeriod;
                    return FinishInit(client, client3, mixFmt, info,
                        ref renderService, ref converter, ref bufferFrames);
                }

                if (hr == WasapiConstants.AUDCLNT_E_UNSUPPORTED_FORMAT && attempt < 3)
                {
                    // Rare: mix format rejected for streaming — hand Windows our float
                    // format and let it convert. Fresh client first.
                    _forceOwnFormat = true;
                    continue;
                }

                if (_forceOwnFormat)
                {
                    _forceOwnFormat = false;
                    ownFmt = WasapiFormat.BuildFloatFormat(info.SampleRate, info.Channels);
                    hr = client.Initialize(WasapiConstants.AUDCLNT_SHAREMODE_SHARED,
                        WasapiConstants.STREAMFLAGS_EVENTCALLBACK
                        | WasapiConstants.STREAMFLAGS_AUTOCONVERTPCM
                        | WasapiConstants.STREAMFLAGS_SRC_DEFAULT_QUALITY,
                        bufferDuration, 0, ownFmt, IntPtr.Zero);
                    if (hr == WasapiConstants.S_OK)
                    {
                        Tier = EndpointTier.DefaultPeriod;
                        var floatInfo = new WasapiFormatInfo(info.SampleRate, info.Channels,
                            MixEncoding.Float32, info.Channels * 4, 32);
                        return FinishInit(client, client3, ownFmt, floatInfo,
                            ref renderService, ref converter, ref bufferFrames);
                    }
                }

                if (attempt >= 3) return false; // R4: out of rungs
            }
            finally
            {
                if (mixFmt != IntPtr.Zero) Marshal.FreeCoTaskMem(mixFmt);
                if (ownFmt != IntPtr.Zero) Marshal.FreeCoTaskMem(ownFmt);
            }
        }
    }

    private bool _lockedPeriodRetry;
    private bool _forceOwnFormat;

    private bool FinishInit(IAudioClient client, IAudioClient3? client3, IntPtr fmt,
        WasapiFormatInfo info, ref IAudioRenderClient? renderService,
        ref SampleConverter? converter, ref uint bufferFrames)
    {
        if (client.SetEventHandle(_frameEvent.SafeWaitHandle.DangerousGetHandle())
            != WasapiConstants.S_OK) return false;
        if (client.GetBufferSize(out bufferFrames) != WasapiConstants.S_OK || bufferFrames == 0)
            return false;

        var iid = WasapiConstants.IID_IAudioRenderClient;
        if (client.GetService(ref iid, out var svc) != WasapiConstants.S_OK || svc is null)
            return false;
        renderService = (IAudioRenderClient)svc;
        converter = new SampleConverter(info.Encoding);

        ActualSampleRate = info.SampleRate;
        ActualChannels = info.Channels;
        ActualBufferMs = (int)Math.Round(bufferFrames * 1000.0 / info.SampleRate);

        // Period: prefer the current shared engine period (what our event cadence
        // actually is); fall back to the default device period.
        double periodMs = 10;
        if (client3 is not null)
        {
            IntPtr curFmt = IntPtr.Zero;
            try
            {
                if (client3.GetCurrentSharedModeEnginePeriod(out curFmt, out var curFrames)
                        == WasapiConstants.S_OK && curFrames > 0)
                {
                    periodMs = curFrames * 1000.0 / info.SampleRate;
                }
            }
            finally
            {
                if (curFmt != IntPtr.Zero) Marshal.FreeCoTaskMem(curFmt);
            }
        }
        else if (client.GetDevicePeriod(out var defPeriod, out _) == WasapiConstants.S_OK && defPeriod > 0)
        {
            periodMs = defPeriod / 10_000.0;
        }
        ActualPeriodMs = periodMs;
        return true;
    }

    private uint TargetFrames(uint bufferFrames)
    {
        int t = _targetPaddingFrames;
        if (t <= 0) return bufferFrames;
        return (uint)Math.Min(bufferFrames, (uint)t);
    }

    private static bool IsFatal(int hr) =>
        hr == WasapiConstants.AUDCLNT_E_DEVICE_INVALIDATED ||
        hr == WasapiConstants.AUDCLNT_E_RESOURCES_INVALIDATED;

    private void Fault(int hr)
    {
        if (Interlocked.Exchange(ref _faultLatch, 1) != 0) return;
        try { Faulted?.Invoke(hr); } catch { }
    }
}

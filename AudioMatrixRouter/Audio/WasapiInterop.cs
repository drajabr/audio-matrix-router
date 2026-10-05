using System.Runtime.InteropServices;

namespace AudioMatrixRouter.Audio.Wasapi;

// Minimal WASAPI COM surface for the high-performance transport. NAudio 2.2.1 does not
// expose IAudioClient3 (small shared-mode periods), MMCSS thread registration, or
// event-driven loopback, so these are declared here directly.

internal static class WasapiConstants
{
    public const int ClsCtxAll = 0x17;
    public const int ShareModeShared = 0;

    public const int StreamFlagsLoopback = 0x00020000;
    public const int StreamFlagsEventCallback = 0x00040000;
    public const int StreamFlagsNoPersist = 0x00080000;

    public const uint BufferFlagsSilent = 0x2;

    public const int AudclntEDeviceInvalidated = unchecked((int)0x88890004);
    public const int AudclntEEnginePeriodicityLocked = unchecked((int)0x88890019);
    public const int AudclntEInvalidStreamFlag = unchecked((int)0x88890007);

    public static readonly Guid IidIAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    public static readonly Guid IidIAudioClient3 = new("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42");
    public static readonly Guid IidIAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
    public static readonly Guid IidIAudioRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
    public static readonly Guid KsDataFormatSubtypePcm = new("00000001-0000-0010-8000-00aa00389b71");
    public static readonly Guid KsDataFormatSubtypeIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");
}

internal static class MMDeviceEnumeratorFactory
{
    private static readonly Guid Clsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    /// <summary>
    /// Creates the enumerator by CLSID and QIs for the interface. A [ComImport] coclass
    /// cannot be used here: the runtime caches one RCW type per CLSID and NAudio already
    /// owns it for this CLSID, so "new" on our own coclass throws InvalidCastException.
    /// </summary>
    public static IMMDeviceEnumerator Create()
    {
        var type = Type.GetTypeFromCLSID(Clsid) ?? throw new InvalidOperationException("MMDeviceEnumerator CLSID unavailable.");
        var obj = Activator.CreateInstance(type) ?? throw new InvalidOperationException("MMDeviceEnumerator activation failed.");
        return (IMMDeviceEnumerator)obj;
    }
}

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    [PreserveSig] int OpenPropertyStore(int access, out IntPtr properties);
    [PreserveSig] int GetId(out IntPtr id);
    [PreserveSig] int GetState(out int state);
}

[ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, ref Guid audioSessionGuid);
    [PreserveSig] int GetBufferSize(out uint bufferFrames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint paddingFrames);
    [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
    [PreserveSig] int GetMixFormat(out IntPtr format);
    [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr eventHandle);
    [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
}

/// <summary>
/// IAudioClient3 — declared flat (IAudioClient + IAudioClient2 + IAudioClient3 methods in
/// vtable order) because .NET COM interop does not compose interface inheritance.
/// </summary>
[ComImport, Guid("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient3
{
    // IAudioClient
    [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, ref Guid audioSessionGuid);
    [PreserveSig] int GetBufferSize(out uint bufferFrames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint paddingFrames);
    [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
    [PreserveSig] int GetMixFormat(out IntPtr format);
    [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr eventHandle);
    [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    // IAudioClient2
    [PreserveSig] int IsOffloadCapable(int category, out int offloadCapable);
    [PreserveSig] int SetClientProperties(IntPtr properties);
    [PreserveSig] int GetBufferSizeLimits(IntPtr format, int eventDriven, out long minBufferDuration, out long maxBufferDuration);
    // IAudioClient3
    [PreserveSig] int GetSharedModeEnginePeriod(IntPtr format, out uint defaultPeriodFrames, out uint fundamentalPeriodFrames, out uint minPeriodFrames, out uint maxPeriodFrames);
    [PreserveSig] int GetCurrentSharedModeEnginePeriod(out IntPtr format, out uint currentPeriodFrames);
    [PreserveSig] int InitializeSharedAudioStream(int streamFlags, uint periodInFrames, IntPtr format, ref Guid audioSessionGuid);
}

[ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    [PreserveSig] int GetBuffer(out IntPtr data, out uint framesToRead, out uint flags, out ulong devicePosition, out ulong qpcPosition);
    [PreserveSig] int ReleaseBuffer(uint framesRead);
    [PreserveSig] int GetNextPacketSize(out uint framesInNextPacket);
}

[ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioRenderClient
{
    [PreserveSig] int GetBuffer(uint framesRequested, out IntPtr data);
    [PreserveSig] int ReleaseBuffer(uint framesWritten, uint flags);
}

internal static class Avrt
{
    public const int AvrtPriorityHigh = 1;
    public const int AvrtPriorityCritical = 2;

    [DllImport("avrt.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "AvSetMmThreadCharacteristicsW")]
    public static extern IntPtr AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);

    [DllImport("avrt.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);

    [DllImport("avrt.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AvSetMmThreadPriority(IntPtr handle, int priority);

    /// <summary>Registers the calling thread with the "Pro Audio" MMCSS class. Returns the handle to revert, or Zero.</summary>
    public static IntPtr EnterProAudio()
    {
        try
        {
            uint taskIndex = 0;
            var handle = AvSetMmThreadCharacteristics("Pro Audio", ref taskIndex);
            if (handle != IntPtr.Zero)
            {
                AvSetMmThreadPriority(handle, AvrtPriorityHigh);
            }
            return handle;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    public static void Leave(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;
        try { AvRevertMmThreadCharacteristics(handle); } catch { }
    }
}

internal enum SampleKind
{
    Float32,
    Pcm16,
    Pcm24,
    Pcm32,
}

/// <summary>
/// Parsed WAVEFORMATEX / WAVEFORMATEXTENSIBLE owned as a CoTaskMem pointer (the mix format
/// returned by the engine). Kept as a pointer so it can be handed straight back to Initialize.
/// </summary>
internal sealed unsafe class MixFormat : IDisposable
{
    public IntPtr Pointer { get; private set; }
    public int Channels { get; }
    public int SampleRate { get; }
    public int BitsPerSample { get; }
    public int BlockAlign { get; }
    public SampleKind Kind { get; }

    private MixFormat(IntPtr pointer, int channels, int sampleRate, int bits, int blockAlign, SampleKind kind)
    {
        Pointer = pointer;
        Channels = channels;
        SampleRate = sampleRate;
        BitsPerSample = bits;
        BlockAlign = blockAlign;
        Kind = kind;
    }

    public static MixFormat FromPointer(IntPtr p)
    {
        if (p == IntPtr.Zero) throw new InvalidOperationException("Null mix format.");
        ushort tag = (ushort)Marshal.ReadInt16(p, 0);
        int channels = (ushort)Marshal.ReadInt16(p, 2);
        int rate = Marshal.ReadInt32(p, 4);
        int blockAlign = (ushort)Marshal.ReadInt16(p, 12);
        int bits = (ushort)Marshal.ReadInt16(p, 14);
        int cbSize = (ushort)Marshal.ReadInt16(p, 16);

        bool isFloat;
        bool isPcm;
        if (tag == 0xFFFE && cbSize >= 22)
        {
            var sub = Marshal.PtrToStructure<Guid>(p + 24);
            isFloat = sub == WasapiConstants.KsDataFormatSubtypeIeeeFloat;
            isPcm = sub == WasapiConstants.KsDataFormatSubtypePcm;
        }
        else
        {
            isFloat = tag == 3;
            isPcm = tag == 1;
        }

        SampleKind kind;
        if (isFloat && bits == 32) kind = SampleKind.Float32;
        else if (isPcm && bits == 16) kind = SampleKind.Pcm16;
        else if (isPcm && bits == 24) kind = SampleKind.Pcm24;
        else if (isPcm && bits == 32) kind = SampleKind.Pcm32;
        else throw new NotSupportedException($"Unsupported mix format: tag={tag} bits={bits}");

        if (channels <= 0 || rate <= 0 || blockAlign <= 0)
        {
            throw new NotSupportedException("Degenerate mix format.");
        }

        return new MixFormat(p, channels, rate, bits, blockAlign, kind);
    }

    /// <summary>Converts <paramref name="frames"/> interleaved device-format frames at <paramref name="src"/> into floats.</summary>
    public void ToFloat(IntPtr src, float[] dst, int frames)
    {
        int samples = frames * Channels;
        switch (Kind)
        {
            case SampleKind.Float32:
                Marshal.Copy(src, dst, 0, samples);
                break;
            case SampleKind.Pcm16:
            {
                short* s = (short*)src;
                const float scale = 1f / 32768f;
                for (int i = 0; i < samples; i++) dst[i] = s[i] * scale;
                break;
            }
            case SampleKind.Pcm24:
            {
                byte* b = (byte*)src;
                const float scale = 1f / 8388608f;
                for (int i = 0; i < samples; i++)
                {
                    int v = (b[0] << 8) | (b[1] << 16) | (b[2] << 24);
                    dst[i] = (v >> 8) * scale;
                    b += 3;
                }
                break;
            }
            case SampleKind.Pcm32:
            {
                int* s = (int*)src;
                const float scale = 1f / 2147483648f;
                for (int i = 0; i < samples; i++) dst[i] = s[i] * scale;
                break;
            }
        }
    }

    /// <summary>Converts <paramref name="frames"/> interleaved float frames into the device format at <paramref name="dst"/>.</summary>
    public void FromFloat(float[] src, IntPtr dst, int frames)
    {
        int samples = frames * Channels;
        switch (Kind)
        {
            case SampleKind.Float32:
                Marshal.Copy(src, 0, dst, samples);
                break;
            case SampleKind.Pcm16:
            {
                short* d = (short*)dst;
                for (int i = 0; i < samples; i++)
                {
                    float v = Math.Clamp(src[i], -1f, 1f) * 32767f;
                    d[i] = (short)v;
                }
                break;
            }
            case SampleKind.Pcm24:
            {
                byte* d = (byte*)dst;
                for (int i = 0; i < samples; i++)
                {
                    int v = (int)(Math.Clamp(src[i], -1f, 1f) * 8388607f);
                    d[0] = (byte)v;
                    d[1] = (byte)(v >> 8);
                    d[2] = (byte)(v >> 16);
                    d += 3;
                }
                break;
            }
            case SampleKind.Pcm32:
            {
                int* d = (int*)dst;
                for (int i = 0; i < samples; i++)
                {
                    double v = Math.Clamp(src[i], -1f, 1f) * 2147483647.0;
                    d[i] = (int)v;
                }
                break;
            }
        }
    }

    public void Dispose()
    {
        if (Pointer != IntPtr.Zero)
        {
            Marshal.FreeCoTaskMem(Pointer);
            Pointer = IntPtr.Zero;
        }
    }
}

/// <summary>
/// One shared-mode WASAPI stream (capture or render) opened with the smallest engine period
/// the driver allows. Encapsulates IAudioClient3 negotiation with graceful fallback to the
/// classic IAudioClient path (default period) on drivers or OS builds without it.
/// </summary>
internal sealed class WasapiSharedStream : IDisposable
{
    private IMMDevice? _device;
    private object? _clientObject;
    private IAudioClient3? _client3;
    private IAudioClient? _client;
    private MixFormat? _format;
    private AutoResetEvent? _event;

    public MixFormat Format => _format ?? throw new InvalidOperationException("Stream not opened.");
    public int SampleRate => Format.SampleRate;
    public int Channels => Format.Channels;
    public uint BufferFrames { get; private set; }
    public uint PeriodFrames { get; private set; }
    public double PeriodMs => SampleRate > 0 ? PeriodFrames * 1000.0 / SampleRate : 0;
    /// <summary>True when an IAudioClient3 small period was negotiated (vs. the default 10 ms class period).</summary>
    public bool LowLatencyPeriod { get; private set; }
    public bool UsingClient3 => _client3 != null;
    public AutoResetEvent Event => _event ?? throw new InvalidOperationException("Stream not initialised.");

    /// <summary>Optional diagnostic sink for negotiation failures (HRESULTs, exceptions).</summary>
    public static Action<string>? Trace { get; set; }
    private static void Log(string message) => Trace?.Invoke(message);

    public static WasapiSharedStream? Open(string endpointId)
    {
        var stream = new WasapiSharedStream();
        try
        {
            var enumerator = MMDeviceEnumeratorFactory.Create();
            int hr = enumerator.GetDevice(endpointId, out var device);
            Marshal.ReleaseComObject(enumerator);
            if (hr < 0 || device == null) { Log($"GetDevice failed hr=0x{hr:X8}"); stream.Dispose(); return null; }
            stream._device = device;

            var iid3 = WasapiConstants.IidIAudioClient3;
            hr = device.Activate(ref iid3, WasapiConstants.ClsCtxAll, IntPtr.Zero, out var obj);
            if (hr >= 0 && obj is IAudioClient3 c3)
            {
                stream._clientObject = obj;
                stream._client3 = c3;
            }
            else
            {
                var iid = WasapiConstants.IidIAudioClient;
                hr = device.Activate(ref iid, WasapiConstants.ClsCtxAll, IntPtr.Zero, out obj);
                if (hr < 0 || obj is not IAudioClient c) { Log($"Activate(IAudioClient) failed hr=0x{hr:X8}"); stream.Dispose(); return null; }
                stream._clientObject = obj;
                stream._client = c;
            }

            IntPtr fmtPtr;
            hr = stream._client3 != null ? stream._client3.GetMixFormat(out fmtPtr) : stream._client!.GetMixFormat(out fmtPtr);
            if (hr < 0 || fmtPtr == IntPtr.Zero) { Log($"GetMixFormat failed hr=0x{hr:X8}"); stream.Dispose(); return null; }
            stream._format = MixFormat.FromPointer(fmtPtr);
            return stream;
        }
        catch (Exception ex)
        {
            Log($"Open threw: {ex.GetType().Name}: {ex.Message}");
            stream.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Initialises the stream event-driven at the minimum shared period. For loopback the
    /// classic Initialize path is used (IAudioClient3 low-latency streams cannot be loopback);
    /// its period then follows the endpoint's current engine period, which a concurrent
    /// low-latency render stream on the same endpoint pulls down.
    /// </summary>
    public bool Initialize(bool loopback, int fallbackBufferMs)
    {
        if (_format == null) return false;
        var sessionGuid = Guid.Empty;
        int flags = WasapiConstants.StreamFlagsEventCallback | (loopback ? WasapiConstants.StreamFlagsLoopback : 0);
        bool initialised = false;

        if (_client3 != null && !loopback)
        {
            int hr = _client3.GetSharedModeEnginePeriod(_format.Pointer, out var defPeriod, out _, out var minPeriod, out _);
            if (hr < 0) Log($"GetSharedModeEnginePeriod hr=0x{hr:X8}");
            if (hr >= 0 && minPeriod > 0)
            {
                hr = _client3.InitializeSharedAudioStream(flags, minPeriod, _format.Pointer, ref sessionGuid);
                if (hr < 0) Log($"InitializeSharedAudioStream(min={minPeriod}) hr=0x{hr:X8}");
                if (hr >= 0)
                {
                    PeriodFrames = minPeriod;
                    LowLatencyPeriod = minPeriod < defPeriod;
                    initialised = true;
                }
                else if (hr == WasapiConstants.AudclntEEnginePeriodicityLocked)
                {
                    // Another client pinned the engine period; join it rather than fail.
                    hr = _client3.GetCurrentSharedModeEnginePeriod(out var curFmt, out var curPeriod);
                    if (curFmt != IntPtr.Zero) Marshal.FreeCoTaskMem(curFmt);
                    if (hr >= 0 && curPeriod > 0)
                    {
                        hr = _client3.InitializeSharedAudioStream(flags, curPeriod, _format.Pointer, ref sessionGuid);
                        if (hr >= 0)
                        {
                            PeriodFrames = curPeriod;
                            LowLatencyPeriod = curPeriod < defPeriod;
                            initialised = true;
                        }
                    }
                }
            }
        }

        if (!initialised)
        {
            long bufferDuration = Math.Max(1, fallbackBufferMs) * 10_000L;
            int hr = _client3 != null
                ? _client3.Initialize(WasapiConstants.ShareModeShared, flags, bufferDuration, 0, _format.Pointer, ref sessionGuid)
                : _client!.Initialize(WasapiConstants.ShareModeShared, flags, bufferDuration, 0, _format.Pointer, ref sessionGuid);
            if (hr < 0) { Log($"Initialize(classic) hr=0x{hr:X8}"); return false; }

            uint period = 0;
            if (_client3 != null)
            {
                int phr = _client3.GetCurrentSharedModeEnginePeriod(out var curFmt, out var curPeriod);
                if (curFmt != IntPtr.Zero) Marshal.FreeCoTaskMem(curFmt);
                if (phr >= 0 && curPeriod > 0) period = curPeriod;
            }
            if (period == 0)
            {
                long defPeriod100ns = 0;
                int phr = _client3 != null
                    ? _client3.GetDevicePeriod(out defPeriod100ns, out _)
                    : _client!.GetDevicePeriod(out defPeriod100ns, out _);
                period = phr >= 0 && defPeriod100ns > 0
                    ? (uint)Math.Max(1, Math.Round(defPeriod100ns / 10_000_000.0 * _format.SampleRate))
                    : (uint)Math.Max(1, _format.SampleRate / 100);
            }
            PeriodFrames = period;
            LowLatencyPeriod = PeriodFrames * 1000.0 / _format.SampleRate < 9.0;
        }

        int bhr = _client3 != null ? _client3.GetBufferSize(out var buf) : _client!.GetBufferSize(out buf);
        if (bhr < 0 || buf == 0) { Log($"GetBufferSize hr=0x{bhr:X8} frames={buf}"); return false; }
        BufferFrames = buf;

        _event = new AutoResetEvent(false);
        int ehr = _client3 != null
            ? _client3.SetEventHandle(_event.SafeWaitHandle.DangerousGetHandle())
            : _client!.SetEventHandle(_event.SafeWaitHandle.DangerousGetHandle());
        if (ehr < 0) Log($"SetEventHandle hr=0x{ehr:X8}");
        return ehr >= 0;
    }

    public T? GetService<T>(Guid iid) where T : class
    {
        try
        {
            int hr = _client3 != null ? _client3.GetService(ref iid, out var svc) : _client!.GetService(ref iid, out svc);
            return hr >= 0 ? svc as T : null;
        }
        catch
        {
            return null;
        }
    }

    public uint GetCurrentPadding()
    {
        int hr = _client3 != null ? _client3.GetCurrentPadding(out var padding) : _client!.GetCurrentPadding(out padding);
        return hr >= 0 ? padding : 0;
    }

    public bool Start()
    {
        int hr = _client3 != null ? _client3.Start() : _client!.Start();
        return hr >= 0;
    }

    public void Stop()
    {
        try { if (_client3 != null) _client3.Stop(); else _client?.Stop(); } catch { }
    }

    public void Dispose()
    {
        Stop();
        try { _event?.Dispose(); } catch { }
        _event = null;
        try { _format?.Dispose(); } catch { }
        _format = null;
        _client3 = null;
        _client = null;
        if (_clientObject != null)
        {
            try { Marshal.ReleaseComObject(_clientObject); } catch { }
            _clientObject = null;
        }
        if (_device != null)
        {
            try { Marshal.ReleaseComObject(_device); } catch { }
            _device = null;
        }
    }
}

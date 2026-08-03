using System.Runtime.InteropServices;

namespace AudioMatrixRouter.Audio.Wasapi;

// ============================================================================
// Hand-rolled WASAPI COM interop. Exists because NAudio's internal IAudioClient
// declaration stops at the IAudioClient vtable — IAudioClient3's small-period
// shared streams (GetSharedModeEnginePeriod / InitializeSharedAudioStream) are
// unreachable through it. Everything here is [PreserveSig]: on the audio path
// HRESULTs are branch decisions (AUDCLNT_E_DEVICE_INVALIDATED → fault, ladder
// rungs → fall back), never exceptions.
//
// x64-only process (csproj Platforms=x64); layouts assume standard Win64 ABI.
// ============================================================================

internal static class WasapiConstants
{
    public const uint CLSCTX_ALL = 0x17;

    // AUDCLNT_SHAREMODE
    public const int AUDCLNT_SHAREMODE_SHARED = 0;

    // AUDCLNT_STREAMFLAGS_*
    public const uint STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    public const uint STREAMFLAGS_LOOPBACK = 0x00020000;
    public const uint STREAMFLAGS_AUTOCONVERTPCM = 0x80000000;
    public const uint STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;

    // AUDCLNT_BUFFERFLAGS_* (capture GetBuffer out-flags / render ReleaseBuffer in-flags)
    public const uint BUFFERFLAGS_DATA_DISCONTINUITY = 0x1;
    public const uint BUFFERFLAGS_SILENT = 0x2;

    // HRESULTs
    public const int S_OK = 0;
    public const int E_NOINTERFACE = unchecked((int)0x80004002);
    public const int E_INVALIDARG = unchecked((int)0x80070057);
    public const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890004);
    public const int AUDCLNT_E_UNSUPPORTED_FORMAT = unchecked((int)0x88890008);
    public const int AUDCLNT_E_BUFFER_ERROR = unchecked((int)0x88890018);
    public const int AUDCLNT_E_INVALID_DEVICE_PERIOD = unchecked((int)0x88890020);
    public const int AUDCLNT_E_RESOURCES_INVALIDATED = unchecked((int)0x88890026);
    public const int AUDCLNT_E_ENGINE_PERIODICITY_LOCKED = unchecked((int)0x88890028);
    public const int AUDCLNT_E_ENGINE_FORMAT_LOCKED = unchecked((int)0x88890029);

    public static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    public static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    public static readonly Guid IID_IAudioClient3 = new("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42");
    public static readonly Guid IID_IAudioRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
    public static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
}

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    // vtable order matters; unused slots are declared to keep offsets correct
    [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams,
        [MarshalAs(UnmanagedType.IUnknown)] out object? iface);
    [PreserveSig] int OpenPropertyStore(int access, out IntPtr properties);
    [PreserveSig] int GetId(out IntPtr strId); // CoTaskMemFree if ever used
    [PreserveSig] int GetState(out int state);
}

/// <summary>
/// Flat IAudioClient3 declaration: COM interface inheritance does not compose
/// vtables across [ComImport] interfaces, so every IAudioClient and IAudioClient2
/// method is re-declared, in slot order, under the IAudioClient3 IID. Only valid
/// on objects that actually QI to IAudioClient3 (Windows 10 1607+ endpoints).
/// </summary>
[ComImport, Guid("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient3
{
    // ----- IAudioClient (slots 3..14) -----
    [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration,
        long periodicity, IntPtr format, IntPtr audioSessionGuid);
    [PreserveSig] int GetBufferSize(out uint bufferFrames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint paddingFrames);
    [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
    [PreserveSig] int GetMixFormat(out IntPtr format); // CoTaskMemFree
    [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr eventHandle);
    [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object? service);
    // ----- IAudioClient2 (slots 15..17) -----
    [PreserveSig] int IsOffloadCapable(int category, out int offloadCapable);
    [PreserveSig] int SetClientProperties(IntPtr properties);
    [PreserveSig] int GetBufferSizeLimits(IntPtr format, int eventDriven,
        out long minBufferDuration, out long maxBufferDuration);
    // ----- IAudioClient3 (slots 18..20) -----
    [PreserveSig] int GetSharedModeEnginePeriod(IntPtr format,
        out uint defaultPeriodFrames, out uint fundamentalPeriodFrames,
        out uint minPeriodFrames, out uint maxPeriodFrames);
    [PreserveSig] int GetCurrentSharedModeEnginePeriod(out IntPtr format, out uint currentPeriodFrames); // format: CoTaskMemFree
    [PreserveSig] int InitializeSharedAudioStream(uint streamFlags, uint periodFrames,
        IntPtr format, IntPtr audioSessionGuid);
}

/// <summary>Plain IAudioClient, for endpoints/OSes where the IAudioClient3 QI fails.</summary>
[ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration,
        long periodicity, IntPtr format, IntPtr audioSessionGuid);
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
    [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object? service);
}

[ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioRenderClient
{
    [PreserveSig] int GetBuffer(uint framesRequested, out IntPtr dataPtr);
    [PreserveSig] int ReleaseBuffer(uint framesWritten, uint flags);
}

[ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    [PreserveSig] int GetBuffer(out IntPtr dataPtr, out uint framesRead, out uint flags,
        out ulong devicePosition, out ulong qpcPosition);
    [PreserveSig] int ReleaseBuffer(uint framesRead);
    [PreserveSig] int GetNextPacketSize(out uint framesInNextPacket);
}

internal static class WasapiActivation
{
    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    private const uint COINIT_MULTITHREADED = 0x0;
    private const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);

    /// <summary>Bracket a client thread with COM init; RPC_E_CHANGED_MODE (already
    /// STA) is tolerated — built-in RCWs still work, just with marshaling cost.
    /// Returns whether CoUninitialize must be called on exit.</summary>
    public static bool EnterComThread()
    {
        int hr = CoInitializeEx(IntPtr.Zero, COINIT_MULTITHREADED);
        return hr >= 0; // S_OK or S_FALSE (already initialized on this thread)
    }

    public static void ExitComThread(bool mustUninit)
    {
        if (mustUninit)
        {
            try { CoUninitialize(); } catch { }
        }
    }

    /// <summary>
    /// Activates the endpoint's audio client from its ID string. Returns the object
    /// as plain IAudioClient plus, when the endpoint supports it, the IAudioClient3
    /// view of the SAME object (QI — one activation). Caller must release via
    /// <see cref="ReleaseActivation"/>.
    /// </summary>
    public static int TryActivate(string endpointId,
        out IMMDeviceEnumerator? enumerator, out IMMDevice? device,
        out IAudioClient? client, out IAudioClient3? client3)
    {
        enumerator = null;
        device = null;
        client = null;
        client3 = null;
        try
        {
            var type = Type.GetTypeFromCLSID(WasapiConstants.CLSID_MMDeviceEnumerator);
            if (type is null) return WasapiConstants.E_NOINTERFACE;
            enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(type)!;

            int hr = enumerator.GetDevice(endpointId, out device);
            if (hr != WasapiConstants.S_OK || device is null) return hr != 0 ? hr : WasapiConstants.E_NOINTERFACE;

            var iid = WasapiConstants.IID_IAudioClient;
            hr = device.Activate(ref iid, WasapiConstants.CLSCTX_ALL, IntPtr.Zero, out var obj);
            if (hr != WasapiConstants.S_OK || obj is null) return hr != 0 ? hr : WasapiConstants.E_NOINTERFACE;

            client = (IAudioClient)obj;
            client3 = obj as IAudioClient3; // QI; null when unsupported
            return WasapiConstants.S_OK;
        }
        catch (COMException ex)
        {
            return ex.HResult;
        }
        catch
        {
            return WasapiConstants.E_NOINTERFACE;
        }
    }

    public static void ReleaseActivation(IMMDeviceEnumerator? enumerator, IMMDevice? device,
        IAudioClient? client)
    {
        // client3 is the same COM identity as client — one release covers both RCW views.
        try { if (client is not null) Marshal.ReleaseComObject(client); } catch { }
        try { if (device is not null) Marshal.ReleaseComObject(device); } catch { }
        try { if (enumerator is not null) Marshal.ReleaseComObject(enumerator); } catch { }
    }
}

internal static class WasapiDiagnostics
{
    private static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), "amr-wasapi-probe.log");

    internal static void Log(string line)
    {
        System.Diagnostics.Debug.WriteLine(line);
        try { File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}"); }
        catch { }
    }

    /// <summary>Logs the shared-mode engine period capabilities of an endpoint
    /// (default/fundamental/min/max, in frames and ms). Diagnostic only.</summary>
    public static void ProbeEnginePeriods(string label, string endpointId)
    {
        var hr = WasapiActivation.TryActivate(endpointId,
            out var enumerator, out var device, out var client, out var client3);
        try
        {
            if (hr != WasapiConstants.S_OK || client is null)
            {
                Log($"[WASAPI] {label}: activate failed 0x{hr:X8}");
                return;
            }
            if (client3 is null)
            {
                Log($"[WASAPI] {label}: IAudioClient3 not supported");
                return;
            }

            IntPtr fmt = IntPtr.Zero;
            try
            {
                if (client3.GetMixFormat(out fmt) != WasapiConstants.S_OK || fmt == IntPtr.Zero) return;
                var info = WasapiFormat.Parse(fmt);
                if (client3.GetSharedModeEnginePeriod(fmt, out var def, out var fund, out var min, out var max)
                    == WasapiConstants.S_OK && info.SampleRate > 0)
                {
                    double toMs = 1000.0 / info.SampleRate;
                    Log($"[WASAPI] {label}: {info.SampleRate}Hz {info.Channels}ch {info.Encoding} — " +
                        $"period default {def}f ({def * toMs:0.00}ms), fundamental {fund}f, " +
                        $"min {min}f ({min * toMs:0.00}ms), max {max}f ({max * toMs:0.00}ms)");
                }
            }
            finally
            {
                if (fmt != IntPtr.Zero) Marshal.FreeCoTaskMem(fmt);
            }
        }
        finally
        {
            WasapiActivation.ReleaseActivation(enumerator, device, client);
        }
    }
}

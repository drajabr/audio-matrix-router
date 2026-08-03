using System.Runtime.InteropServices;

namespace AudioMatrixRouter.Audio.Wasapi;

internal enum MixEncoding
{
    Float32,
    Pcm16,
    Pcm24,
    Pcm32,
    Other,
}

internal readonly record struct WasapiFormatInfo(
    int SampleRate, int Channels, MixEncoding Encoding, int BlockAlign, int BitsPerSample);

/// <summary>
/// WAVEFORMATEX / WAVEFORMATEXTENSIBLE reading and building without struct
/// marshaling (field-by-field via Marshal — layout is fixed and tiny), plus the
/// float↔PCM conversions used when an endpoint's mix format is not float32.
/// </summary>
internal static class WasapiFormat
{
    private const ushort WAVE_FORMAT_PCM = 1;
    private const ushort WAVE_FORMAT_IEEE_FLOAT = 3;
    private const ushort WAVE_FORMAT_EXTENSIBLE = 0xFFFE;

    private static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00AA00389B71");
    private static readonly Guid SubtypeFloat = new("00000003-0000-0010-8000-00AA00389B71");

    public static WasapiFormatInfo Parse(IntPtr fmt)
    {
        ushort tag = (ushort)Marshal.ReadInt16(fmt, 0);
        int channels = Marshal.ReadInt16(fmt, 2);
        int rate = Marshal.ReadInt32(fmt, 4);
        int blockAlign = Marshal.ReadInt16(fmt, 12);
        int bits = Marshal.ReadInt16(fmt, 14);

        MixEncoding enc;
        if (tag == WAVE_FORMAT_IEEE_FLOAT && bits == 32)
        {
            enc = MixEncoding.Float32;
        }
        else if (tag == WAVE_FORMAT_PCM)
        {
            enc = PcmEncodingFor(bits);
        }
        else if (tag == WAVE_FORMAT_EXTENSIBLE)
        {
            // SubFormat GUID at offset 24 (18 header + 2 valid bits + 4 channel mask)
            var guidBytes = new byte[16];
            Marshal.Copy(fmt + 24, guidBytes, 0, 16);
            var sub = new Guid(guidBytes);
            if (sub == SubtypeFloat && bits == 32) enc = MixEncoding.Float32;
            else if (sub == SubtypePcm) enc = PcmEncodingFor(bits);
            else enc = MixEncoding.Other;
        }
        else
        {
            enc = MixEncoding.Other;
        }

        return new WasapiFormatInfo(rate, channels, enc, blockAlign, bits);
    }

    private static MixEncoding PcmEncodingFor(int bits) => bits switch
    {
        16 => MixEncoding.Pcm16,
        24 => MixEncoding.Pcm24,
        32 => MixEncoding.Pcm32,
        _ => MixEncoding.Other,
    };

    /// <summary>Builds an IEEE-float WAVEFORMATEXTENSIBLE in CoTaskMem (caller frees
    /// with Marshal.FreeCoTaskMem). Used for the default-period AUTOCONVERTPCM rung.</summary>
    public static IntPtr BuildFloatFormat(int sampleRate, int channels)
    {
        const int size = 40; // 18 (WAVEFORMATEX) + 22 (EXTENSIBLE tail)
        IntPtr fmt = Marshal.AllocCoTaskMem(size);
        int blockAlign = channels * 4;
        Marshal.WriteInt16(fmt, 0, unchecked((short)WAVE_FORMAT_EXTENSIBLE));
        Marshal.WriteInt16(fmt, 2, (short)channels);
        Marshal.WriteInt32(fmt, 4, sampleRate);
        Marshal.WriteInt32(fmt, 8, sampleRate * blockAlign);
        Marshal.WriteInt16(fmt, 12, (short)blockAlign);
        Marshal.WriteInt16(fmt, 14, 32);            // bits per sample
        Marshal.WriteInt16(fmt, 16, 22);            // cbSize
        Marshal.WriteInt16(fmt, 18, 32);            // valid bits
        Marshal.WriteInt32(fmt, 20, 0);             // channel mask: let the engine decide
        Marshal.Copy(SubtypeFloat.ToByteArray(), 0, fmt + 24, 16);
        return fmt;
    }
}

/// <summary>
/// Per-client sample converter between the engine's interleaved float32 and the
/// endpoint mix encoding. Float32 mix (the normal case) short-circuits to plain
/// Marshal.Copy. Scratch arrays grow geometrically and are never shrunk — no
/// allocation in steady state.
/// </summary>
internal sealed class SampleConverter
{
    private readonly MixEncoding _encoding;
    private short[] _s16 = [];
    private int[] _s32 = [];
    private byte[] _b24 = [];

    public SampleConverter(MixEncoding encoding) => _encoding = encoding;

    /// <summary>float[] source → device buffer at <paramref name="dest"/>.</summary>
    public void WriteToDevice(float[] source, int samples, IntPtr dest)
    {
        switch (_encoding)
        {
            case MixEncoding.Float32:
                Marshal.Copy(source, 0, dest, samples);
                break;
            case MixEncoding.Pcm16:
                if (_s16.Length < samples) _s16 = new short[Grow(_s16.Length, samples)];
                for (int i = 0; i < samples; i++)
                {
                    var v = source[i];
                    v = v > 1f ? 1f : v < -1f ? -1f : v;
                    _s16[i] = (short)(v * 32767f);
                }
                Marshal.Copy(_s16, 0, dest, samples);
                break;
            case MixEncoding.Pcm32:
                if (_s32.Length < samples) _s32 = new int[Grow(_s32.Length, samples)];
                for (int i = 0; i < samples; i++)
                {
                    var v = source[i];
                    v = v > 1f ? 1f : v < -1f ? -1f : v;
                    _s32[i] = (int)(v * 2147483392f); // int.MaxValue rounded down to float-exact
                }
                Marshal.Copy(_s32, 0, dest, samples);
                break;
            case MixEncoding.Pcm24:
                int bytes = samples * 3;
                if (_b24.Length < bytes) _b24 = new byte[Grow(_b24.Length, bytes)];
                for (int i = 0; i < samples; i++)
                {
                    var v = source[i];
                    v = v > 1f ? 1f : v < -1f ? -1f : v;
                    int s = (int)(v * 8388607f);
                    int o = i * 3;
                    _b24[o] = (byte)s;
                    _b24[o + 1] = (byte)(s >> 8);
                    _b24[o + 2] = (byte)(s >> 16);
                }
                Marshal.Copy(_b24, 0, dest, bytes);
                break;
            default:
                throw new NotSupportedException("unconvertible mix encoding");
        }
    }

    /// <summary>Device buffer at <paramref name="source"/> → float[] destination.</summary>
    public void ReadFromDevice(IntPtr source, float[] dest, int samples)
    {
        switch (_encoding)
        {
            case MixEncoding.Float32:
                Marshal.Copy(source, dest, 0, samples);
                break;
            case MixEncoding.Pcm16:
                if (_s16.Length < samples) _s16 = new short[Grow(_s16.Length, samples)];
                Marshal.Copy(source, _s16, 0, samples);
                for (int i = 0; i < samples; i++) dest[i] = _s16[i] / 32768f;
                break;
            case MixEncoding.Pcm32:
                if (_s32.Length < samples) _s32 = new int[Grow(_s32.Length, samples)];
                Marshal.Copy(source, _s32, 0, samples);
                for (int i = 0; i < samples; i++) dest[i] = _s32[i] / 2147483648f;
                break;
            case MixEncoding.Pcm24:
                int bytes = samples * 3;
                if (_b24.Length < bytes) _b24 = new byte[Grow(_b24.Length, bytes)];
                Marshal.Copy(source, _b24, 0, bytes);
                for (int i = 0; i < samples; i++)
                {
                    int o = i * 3;
                    int s = _b24[o] | (_b24[o + 1] << 8) | ((sbyte)_b24[o + 2] << 16);
                    dest[i] = s / 8388608f;
                }
                break;
            default:
                throw new NotSupportedException("unconvertible mix encoding");
        }
    }

    private static int Grow(int current, int needed) => Math.Max(needed, Math.Max(256, current * 2));
}

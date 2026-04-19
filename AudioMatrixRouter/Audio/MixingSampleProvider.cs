using NAudio.Wave;

namespace AudioMatrixRouter.Audio;

/// <summary>
/// ISampleProvider that reads from capture ring buffers, applies routing matrix gains,
/// and mixes into the output for a specific render device.
/// </summary>
public class MixingSampleProvider : ISampleProvider
{
    private readonly RoutingMatrix _matrix;
    private readonly List<CaptureSource> _sources;
    private readonly int _outputChannelOffset;
    private readonly int _outputChannels;
    private readonly WaveFormat _waveFormat;
    private float[] _tempBuffer = [];

    public record struct CaptureSource(RingBuffer Buffer, int GlobalChannelOffset, int Channels);

    public MixingSampleProvider(
        RoutingMatrix matrix,
        List<CaptureSource> sources,
        int outputChannelOffset,
        int outputChannels,
        int sampleRate)
    {
        _matrix = matrix;
        _sources = sources;
        _outputChannelOffset = outputChannelOffset;
        _outputChannels = outputChannels;
        _waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, outputChannels);
    }

    public WaveFormat WaveFormat => _waveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int frames = count / _outputChannels;

        // Zero the output buffer
        Array.Clear(buffer, offset, count);

        var front = _matrix.GetFrontBuffer();
        int matOutCh = _matrix.OutputChannels;

        foreach (var src in _sources)
        {
            // Ensure temp buffer large enough
            int srcSamples = frames * src.Channels;
            if (_tempBuffer.Length < srcSamples)
                _tempBuffer = new float[srcSamples];

            // Read from capture ring buffer
            int framesRead = src.Buffer.Peek(_tempBuffer, 0, frames);
            if (framesRead == 0) continue;

            // Apply routing matrix
            for (int f = 0; f < framesRead; f++)
            {
                for (int srcCh = 0; srcCh < src.Channels; srcCh++)
                {
                    int globalInCh = src.GlobalChannelOffset + srcCh;

                    for (int dstCh = 0; dstCh < _outputChannels; dstCh++)
                    {
                        int globalOutCh = _outputChannelOffset + dstCh;
                        int matIdx = globalInCh * matOutCh + globalOutCh;
                        if (matIdx < 0 || matIdx >= front.Length) continue;

                        ref var cp = ref front[matIdx];
                        if (!cp.Active) continue;

                        float sample = _tempBuffer[f * src.Channels + srcCh] * cp.Gain;
                        buffer[offset + f * _outputChannels + dstCh] += sample;
                    }
                }
            }

            // Consume the frames we read
            src.Buffer.Read(_tempBuffer, 0, framesRead);
        }

        // Hard clamp
        for (int i = 0; i < count; i++)
        {
            ref float s = ref buffer[offset + i];
            if (s > 1f) s = 1f;
            else if (s < -1f) s = -1f;
        }

        return count;
    }
}

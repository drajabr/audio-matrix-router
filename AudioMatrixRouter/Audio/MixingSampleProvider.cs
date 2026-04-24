using NAudio.Wave;
using System.Threading;

namespace AudioMatrixRouter.Audio;

public sealed class OutputSyncCoordinator
{
    private const double MaxPpmCorrection = 200.0;
    private const double RatioSmoothing = 0.15;

    private readonly object _syncLock = new();
    private readonly Dictionary<string, OutputState> _states = new(StringComparer.Ordinal);
    private readonly string _masterConsumerId;

    private sealed class OutputState
    {
        public long FramesRendered;
        public double Ratio = 1.0;
    }

    public OutputSyncCoordinator(string masterConsumerId)
    {
        _masterConsumerId = masterConsumerId;
    }

    public void RegisterConsumer(string consumerId)
    {
        lock (_syncLock)
        {
            if (!_states.ContainsKey(consumerId))
            {
                _states[consumerId] = new OutputState();
            }
        }
    }

    public void RemoveConsumer(string consumerId)
    {
        lock (_syncLock)
        {
            _states.Remove(consumerId);
        }
    }

    public void OnFramesRendered(string consumerId, int frames)
    {
        if (frames <= 0) return;

        lock (_syncLock)
        {
            if (!_states.TryGetValue(consumerId, out var state)) return;

            state.FramesRendered += frames;
            if (consumerId == _masterConsumerId)
            {
                state.Ratio = 1.0;
                return;
            }

            if (!_states.TryGetValue(_masterConsumerId, out var masterState))
            {
                return;
            }

            long frameError = masterState.FramesRendered - state.FramesRendered;
            double maxRatioDelta = MaxPpmCorrection / 1_000_000.0;
            double target = 1.0 + Math.Clamp(frameError * 1e-7, -maxRatioDelta, maxRatioDelta);
            state.Ratio += (target - state.Ratio) * RatioSmoothing;
        }
    }

    public double GetConsumerRatio(string consumerId)
    {
        lock (_syncLock)
        {
            if (!_states.TryGetValue(consumerId, out var state)) return 1.0;
            return state.Ratio;
        }
    }
}

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
    private readonly int _sampleRate;
    private readonly string _consumerId;
    private readonly OutputSyncCoordinator _syncCoordinator;
    private readonly object _delayLock = new();
    private readonly WaveFormat _waveFormat;
    private float[] _sourceTempBuffer = [];
    private float[] _mixBuffer = [];
    private float[] _resampleBuffer = [];
    private float[] _delayBuffer = [];
    private int _delayWriteIndex;
    private double _sourceFrameAccumulator;
    private long _underrunCount;

    public record struct CaptureSource(RingBuffer Buffer, int GlobalChannelOffset, int Channels);

    public MixingSampleProvider(
        RoutingMatrix matrix,
        List<CaptureSource> sources,
        int outputChannelOffset,
        int outputChannels,
        int sampleRate,
        int outputDelayMs,
        string consumerId,
        OutputSyncCoordinator syncCoordinator)
    {
        _matrix = matrix;
        _sources = sources;
        _outputChannelOffset = outputChannelOffset;
        _outputChannels = outputChannels;
        _sampleRate = sampleRate;
        _consumerId = consumerId;
        _syncCoordinator = syncCoordinator;
        _waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, outputChannels);
        _syncCoordinator.RegisterConsumer(_consumerId);
        SetOutputDelayMs(outputDelayMs);
    }

    public WaveFormat WaveFormat => _waveFormat;
    public long UnderrunCount => Interlocked.Read(ref _underrunCount);

    public void SetOutputDelayMs(int delayMs)
    {
        var delayFrames = Math.Clamp((int)Math.Round(_sampleRate * (delayMs / 1000.0)), 0, _sampleRate * 5);
        var delaySamples = delayFrames * _outputChannels;

        lock (_delayLock)
        {
            if (delaySamples <= 0)
            {
                _delayBuffer = [];
                _delayWriteIndex = 0;
                return;
            }

            _delayBuffer = new float[delaySamples];
            _delayWriteIndex = 0;
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int frames = count / _outputChannels;
        if (frames <= 0) return 0;

        double ratio = _syncCoordinator.GetConsumerRatio(_consumerId);
        double desiredSourceFrames = frames * ratio + _sourceFrameAccumulator;
        int sourceFrames = Math.Max(1, (int)Math.Floor(desiredSourceFrames));
        _sourceFrameAccumulator = desiredSourceFrames - sourceFrames;

        int sourceSamples = sourceFrames * _outputChannels;
        if (_mixBuffer.Length < sourceSamples)
            _mixBuffer = new float[sourceSamples];

        Array.Clear(_mixBuffer, 0, sourceSamples);

        var front = _matrix.GetFrontBuffer();
        int matOutCh = _matrix.OutputChannels;

        foreach (var src in _sources)
        {
            // Ensure temp buffer large enough
            int srcSamples = sourceFrames * src.Channels;
            if (_sourceTempBuffer.Length < srcSamples)
                _sourceTempBuffer = new float[srcSamples];

            // Read from capture ring buffer
            int framesRead = src.Buffer.PeekForConsumer(_consumerId, _sourceTempBuffer, 0, sourceFrames);
            if (framesRead == 0)
            {
                Interlocked.Increment(ref _underrunCount);
                continue;
            }

            if (framesRead < sourceFrames)
            {
                Interlocked.Increment(ref _underrunCount);
            }

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

                        float sample = _sourceTempBuffer[f * src.Channels + srcCh] * cp.Gain;
                        _mixBuffer[f * _outputChannels + dstCh] += sample;
                    }
                }
            }

            // Consume the frames we read
            src.Buffer.ReadForConsumer(_consumerId, _sourceTempBuffer, 0, framesRead);
        }

        if (sourceFrames == frames)
        {
            for (int i = 0; i < count; i++)
            {
                ref float s = ref _mixBuffer[i];
                if (s > 1f) s = 1f;
                else if (s < -1f) s = -1f;

                buffer[offset + i] = s;
            }
        }
        else
        {
            if (_resampleBuffer.Length < count)
                _resampleBuffer = new float[count];

            ResampleLinear(_mixBuffer, sourceFrames, _resampleBuffer, frames);
            for (int i = 0; i < count; i++)
            {
                ref float s = ref _resampleBuffer[i];
                if (s > 1f) s = 1f;
                else if (s < -1f) s = -1f;

                buffer[offset + i] = s;
            }
        }

        ApplyOutputDelay(buffer, offset, count);
        _syncCoordinator.OnFramesRendered(_consumerId, frames);

        return count;
    }

    private void ResampleLinear(float[] source, int sourceFrames, float[] destination, int destinationFrames)
    {
        if (destinationFrames <= 0) return;

        if (sourceFrames <= 1)
        {
            for (int f = 0; f < destinationFrames; f++)
            {
                for (int ch = 0; ch < _outputChannels; ch++)
                {
                    destination[f * _outputChannels + ch] = sourceFrames == 1 ? source[ch] : 0f;
                }
            }
            return;
        }

        if (destinationFrames == 1)
        {
            for (int ch = 0; ch < _outputChannels; ch++)
            {
                destination[ch] = source[ch];
            }
            return;
        }

        float scale = (sourceFrames - 1f) / (destinationFrames - 1f);
        for (int outFrame = 0; outFrame < destinationFrames; outFrame++)
        {
            float srcPos = outFrame * scale;
            int srcIndex0 = (int)srcPos;
            int srcIndex1 = Math.Min(srcIndex0 + 1, sourceFrames - 1);
            float frac = srcPos - srcIndex0;

            int outBase = outFrame * _outputChannels;
            int srcBase0 = srcIndex0 * _outputChannels;
            int srcBase1 = srcIndex1 * _outputChannels;

            for (int ch = 0; ch < _outputChannels; ch++)
            {
                float a = source[srcBase0 + ch];
                float b = source[srcBase1 + ch];
                destination[outBase + ch] = a + (b - a) * frac;
            }
        }
    }

    public void DetachConsumer()
    {
        foreach (var source in _sources)
        {
            source.Buffer.RemoveConsumer(_consumerId);
        }

        _syncCoordinator.RemoveConsumer(_consumerId);
    }

    private void ApplyOutputDelay(float[] buffer, int offset, int count)
    {
        lock (_delayLock)
        {
            if (_delayBuffer.Length == 0)
            {
                return;
            }

            for (int i = 0; i < count; i++)
            {
                var delayedSample = _delayBuffer[_delayWriteIndex];
                _delayBuffer[_delayWriteIndex] = buffer[offset + i];
                buffer[offset + i] = delayedSample;

                _delayWriteIndex++;
                if (_delayWriteIndex >= _delayBuffer.Length)
                {
                    _delayWriteIndex = 0;
                }
            }
        }
    }
}

using NAudio.Wave;
using System.Threading;

namespace AudioMatrixRouter.Audio;

public sealed class OutputSyncCoordinator
{
    private const int DriftThresholdFrames = 96;
    private const int CorrectionCooldownFrames = 4096;
    private const int WarmupFrames = 48000;

    private readonly object _syncLock = new();
    private readonly Dictionary<string, OutputState> _states = new(StringComparer.Ordinal);
    private readonly string _masterConsumerId;

    private sealed class OutputState
    {
        public long FramesRendered;
        public double Ratio = 1.0;
        public int PendingFrameSlip;
        public long NextCorrectionAt;
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
            state.Ratio = 1.0;

            if (consumerId == _masterConsumerId) return;
            if (state.PendingFrameSlip != 0) return;

            if (!_states.TryGetValue(_masterConsumerId, out var masterState)) return;
            if (state.FramesRendered < WarmupFrames || masterState.FramesRendered < WarmupFrames) return;
            if (state.FramesRendered < state.NextCorrectionAt) return;

            long errorFrames = masterState.FramesRendered - state.FramesRendered;
            if (errorFrames >= DriftThresholdFrames)
            {
                // Follower is behind master: consume one extra source frame this callback.
                state.PendingFrameSlip = 1;
                state.NextCorrectionAt = state.FramesRendered + CorrectionCooldownFrames;
            }
            else if (errorFrames <= -DriftThresholdFrames)
            {
                // Follower is ahead of master: consume one fewer source frame this callback.
                state.PendingFrameSlip = -1;
                state.NextCorrectionAt = state.FramesRendered + CorrectionCooldownFrames;
            }
        }
    }

    public double GetConsumerRatio(string consumerId)
    {
        _ = consumerId;
        return 1.0;
    }

    public int ConsumeFrameSlip(string consumerId)
    {
        lock (_syncLock)
        {
            if (!_states.TryGetValue(consumerId, out var state)) return 0;
            int slip = state.PendingFrameSlip;
            state.PendingFrameSlip = 0;
            return slip;
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
    private float[] _delayBuffer = [];
    private int _delayWriteIndex;
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
        int slip = _syncCoordinator.ConsumeFrameSlip(_consumerId);
        int sourceFrames = Math.Max(1, frames + slip);

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

        FitMixedFramesToOutput(buffer, offset, frames, sourceFrames);

        ApplyOutputDelay(buffer, offset, count);
        _syncCoordinator.OnFramesRendered(_consumerId, frames);

        return count;
    }

    private void FitMixedFramesToOutput(float[] output, int outputOffset, int outputFrames, int sourceFrames)
    {
        if (sourceFrames == outputFrames)
        {
            for (int i = 0; i < outputFrames * _outputChannels; i++)
            {
                output[outputOffset + i] = ClampSample(_mixBuffer[i]);
            }
            return;
        }

        if (sourceFrames == outputFrames + 1)
        {
            int dropIndex = sourceFrames / 2;
            for (int outFrame = 0; outFrame < outputFrames; outFrame++)
            {
                int srcFrame = outFrame < dropIndex ? outFrame : outFrame + 1;
                int outBase = outputOffset + outFrame * _outputChannels;
                int srcBase = srcFrame * _outputChannels;

                for (int ch = 0; ch < _outputChannels; ch++)
                {
                    float value = _mixBuffer[srcBase + ch];
                    if (outFrame == dropIndex && dropIndex > 0 && dropIndex < sourceFrames - 1)
                    {
                        int prevBase = (dropIndex - 1) * _outputChannels;
                        int nextBase = (dropIndex + 1) * _outputChannels;
                        value = (_mixBuffer[prevBase + ch] + _mixBuffer[nextBase + ch]) * 0.5f;
                    }

                    output[outBase + ch] = ClampSample(value);
                }
            }
            return;
        }

        if (sourceFrames + 1 == outputFrames)
        {
            int insertIndex = sourceFrames / 2;
            for (int outFrame = 0; outFrame < outputFrames; outFrame++)
            {
                int srcFrame = outFrame <= insertIndex ? outFrame : outFrame - 1;
                srcFrame = Math.Clamp(srcFrame, 0, sourceFrames - 1);

                int outBase = outputOffset + outFrame * _outputChannels;
                int srcBase = srcFrame * _outputChannels;

                for (int ch = 0; ch < _outputChannels; ch++)
                {
                    float value = _mixBuffer[srcBase + ch];
                    if (outFrame == insertIndex && insertIndex > 0)
                    {
                        int prevBase = (insertIndex - 1) * _outputChannels;
                        int curBase = Math.Min(insertIndex, sourceFrames - 1) * _outputChannels;
                        value = (_mixBuffer[prevBase + ch] + _mixBuffer[curBase + ch]) * 0.5f;
                    }

                    output[outBase + ch] = ClampSample(value);
                }
            }
            return;
        }

        int framesToCopy = Math.Min(outputFrames, sourceFrames);
        int samplesToCopy = framesToCopy * _outputChannels;
        for (int i = 0; i < samplesToCopy; i++)
        {
            output[outputOffset + i] = ClampSample(_mixBuffer[i]);
        }

        for (int i = samplesToCopy; i < outputFrames * _outputChannels; i++)
        {
            output[outputOffset + i] = 0f;
        }
    }

    private static float ClampSample(float sample)
    {
        if (sample > 1f) return 1f;
        if (sample < -1f) return -1f;
        return sample;
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

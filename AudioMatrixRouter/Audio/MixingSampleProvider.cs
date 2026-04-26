using NAudio.Wave;
using System.Threading;

namespace AudioMatrixRouter.Audio;

public sealed class OutputSyncCoordinator
{
    // ===== Core Sync Constants =====
    private const int WarmupFrames = 48000;  // ~1 second at 48kHz; allow system to stabilize before correcting
    
    // ===== Smoothing =====
    private const double BufferedFramesSmoothingAlpha = 0.05;    // EMA for buffered frame measurements
    private const double ErrorSmoothingAlpha = 0.06;            // EMA for error signal
    private const double TargetSmoothingAlpha = 0.004;          // Single speed for master target convergence
    
    // ===== Follower Sync =====
    private const double RatioGainPpmPerFrame = 2.0;            // PPM adjustment per frame of error
    private const double RatioSmoothingAlpha = 0.08;            // EMA for playback speed correction
    private const int StableSettleBandFrames = 64;              // Don't correct tiny errors
    
    // ===== Spike Rejection =====
    private const int SpikeRejectThresholdFramesBase = 240;     // ~5ms spike threshold
    private const double SpikeBlendAlpha = 0.005;               // Slowly learn spike pattern
    
    // ===== Underrun Recovery =====
    private const double MinTargetDuringUnderrunFraction = 0.5; // Drop floor to 50% during underruns
    private const double MinTargetRebuildAlpha = 0.0001;        // Very slow floor rebuild (imperceptible)

    private readonly object _syncLock = new();
    private readonly Dictionary<string, OutputState> _states = new(StringComparer.Ordinal);
    private string _masterConsumerId;
    private int _baseMasterTargetFrames;
    private int _maxMasterTargetFrames;
    private double _adaptiveMasterTargetFrames;
    private long _totalMasterUnderruns = 0;
    private long _recentUnderrunCount = 0;
    private long _recentSampleCount = 0;
    private double _effectiveMinTargetFrames = -1; // -1 means uninitialized

    private sealed class OutputState
    {
        public long FramesRendered;
        public double Ratio = 1.0;
        public int PendingFrameSlip;
        public long CorrectionCount;
        public int BufferedFrames = -1;
        public double SmoothedBufferedFrames = -1;
        public double SmoothedErrorFrames;
    }

    public OutputSyncCoordinator(string masterConsumerId, int baseMasterTargetFrames, int maxMasterTargetFrames)
    {
        _masterConsumerId = masterConsumerId;
        _baseMasterTargetFrames = Math.Max(1, baseMasterTargetFrames);
        _maxMasterTargetFrames = Math.Max(_baseMasterTargetFrames, maxMasterTargetFrames);
        _adaptiveMasterTargetFrames = _baseMasterTargetFrames;
    }

    public void SetMasterConsumer(string masterConsumerId)
    {
        lock (_syncLock)
        {
            _masterConsumerId = masterConsumerId;
            foreach (var state in _states.Values)
            {
                state.PendingFrameSlip = 0;
                state.BufferedFrames = -1;
                state.SmoothedBufferedFrames = -1;
                state.SmoothedErrorFrames = 0;
                state.CorrectionCount = 0;
                state.Ratio = 1.0;
            }

            _adaptiveMasterTargetFrames = _baseMasterTargetFrames;
            _effectiveMinTargetFrames = _baseMasterTargetFrames;
            _recentUnderrunCount = 0;
        }
    }

    public void SetMasterBufferTarget(int baseMasterTargetFrames, int maxMasterTargetFrames)
    {
        lock (_syncLock)
        {
            _baseMasterTargetFrames = Math.Max(1, baseMasterTargetFrames);
            _maxMasterTargetFrames = Math.Max(_baseMasterTargetFrames, maxMasterTargetFrames);
            _adaptiveMasterTargetFrames = Math.Clamp(_adaptiveMasterTargetFrames, _baseMasterTargetFrames, _maxMasterTargetFrames);
            // When user changes the output buffer floor, do NOT instantly set _effectiveMinTargetFrames to the new base.
            // This would cause a sudden pause as the buffer fills. Instead, let _effectiveMinTargetFrames gradually
            // interpolate toward the new base via MinTargetRebuildAlpha so the transition is smooth and imperceptible.
            // Only ensure it stays within valid bounds on the lower end.
            if (_effectiveMinTargetFrames >= 0)
            {
                // Clamp only downward to prevent going below 1, never clamp upward (that causes instant pause)
                _effectiveMinTargetFrames = Math.Max(_effectiveMinTargetFrames, 1);
                // Allow the cap to gradually rise toward new max if needed, but let it rebuild naturally
                if (_effectiveMinTargetFrames > _maxMasterTargetFrames)
                {
                    _effectiveMinTargetFrames = _maxMasterTargetFrames;
                }
            }
            else
            {
                _effectiveMinTargetFrames = _baseMasterTargetFrames;
            }
            _recentUnderrunCount = 0;
        }
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
        }
    }

    public void ReportBufferedFrames(string consumerId, int bufferedFrames)
    {
        lock (_syncLock)
        {
            if (!_states.TryGetValue(consumerId, out var state)) return;

            state.BufferedFrames = Math.Max(0, bufferedFrames);

            // Spike rejection: blend large deviations slowly into the EMA to gradually learn device envelope
            bool isSpike = state.SmoothedBufferedFrames >= 0
                && state.FramesRendered >= WarmupFrames
                && Math.Abs(state.BufferedFrames - state.SmoothedBufferedFrames) > SpikeRejectThresholdFramesBase;

            double emaAlpha = isSpike ? SpikeBlendAlpha : BufferedFramesSmoothingAlpha;
            state.SmoothedBufferedFrames = state.SmoothedBufferedFrames < 0
                ? state.BufferedFrames
                : (state.SmoothedBufferedFrames * (1.0 - emaAlpha)) + (state.BufferedFrames * emaAlpha);

            // Update adaptive master target based on aggregate starvation
            RecomputeAdaptiveMasterTargetNoLock();

            if (!_states.TryGetValue(_masterConsumerId, out var masterState)) return;
            
            if (consumerId == _masterConsumerId)
            {
                // Master: error is distance from adaptive target
                double masterTargetError = state.SmoothedBufferedFrames - _adaptiveMasterTargetFrames;
                state.SmoothedErrorFrames = state.SmoothedErrorFrames == 0
                    ? masterTargetError
                    : (state.SmoothedErrorFrames * (1.0 - ErrorSmoothingAlpha)) + (masterTargetError * ErrorSmoothingAlpha);
                return;
            }

            if (state.BufferedFrames < 0 || masterState.SmoothedBufferedFrames < 0)
            {
                return;
            }

            // Follower: error is delta from master (0-spread target)
            double errorFrames = state.SmoothedBufferedFrames - masterState.SmoothedBufferedFrames;
            state.SmoothedErrorFrames = state.SmoothedErrorFrames == 0
                ? errorFrames
                : (state.SmoothedErrorFrames * (1.0 - ErrorSmoothingAlpha)) + (errorFrames * ErrorSmoothingAlpha);
        }
    }

    public double GetConsumerRatio(string consumerId)
    {
        lock (_syncLock)
        {
            return _states.TryGetValue(consumerId, out var state)
                ? state.Ratio
                : 1.0;
        }
    }

    public int ConsumeFrameSlip(string consumerId)
    {
        lock (_syncLock)
        {
            if (!_states.TryGetValue(consumerId, out var state)) return 0;

            if (state.PendingFrameSlip == 0)
            {
                state.PendingFrameSlip = ComputePendingSlipNoLock(consumerId, state);
            }

            int slip = state.PendingFrameSlip;
            state.PendingFrameSlip = 0;
            return slip;
        }
    }

    public long GetCorrectionCount(string consumerId)
    {
        lock (_syncLock)
        {
            return _states.TryGetValue(consumerId, out var state)
                ? state.CorrectionCount
                : 0;
        }
    }

    public double GetConsumerTargetFrames(string consumerId)
    {
        lock (_syncLock)
        {
            if (!_states.TryGetValue(consumerId, out var state)) return 0;

            if (consumerId == _masterConsumerId)
            {
                return _adaptiveMasterTargetFrames;
            }

            // For followers: always target exactly the master's current buffer level (0 spread).
            // This ensures followers stay perfectly synced with master at master's pace, no variation band.
            if (_states.TryGetValue(_masterConsumerId, out var masterState))
            {
                var masterFrames = masterState.SmoothedBufferedFrames >= 0
                    ? masterState.SmoothedBufferedFrames
                    : masterState.BufferedFrames;
                // Return master's exact level; ignore learned bias to enforce tight sync
                return Math.Max(0, masterFrames);
            }

            return state.SmoothedBufferedFrames >= 0 ? state.SmoothedBufferedFrames : Math.Max(0, state.BufferedFrames);
        }
    }

    public double GetConsumerVariationRangeFrames(string consumerId)
    {
        lock (_syncLock)
        {
            if (!_states.TryGetValue(consumerId, out var state)) return 0;
            
            // For followers: always report 0 spread since they're exactly following master.
            // Master doesn't need to report variation either (return 0 for all).
            return 0;
        }
    }

    private void RecomputeAdaptiveMasterTargetNoLock()
    {
        int desiredTargetFrames = _baseMasterTargetFrames;
        
        // Check for follower starvation: if any follower is below a safe margin, lift master target
        int minFollowerFrames = int.MaxValue;
        bool hasFollowerState = false;

        foreach (var pair in _states)
        {
            if (pair.Key == _masterConsumerId) continue;
            if (pair.Value.BufferedFrames < 0) continue;
            minFollowerFrames = Math.Min(minFollowerFrames, pair.Value.BufferedFrames);
            hasFollowerState = true;
        }

        // If any follower is starving, increase master target to refill it
        if (hasFollowerState && minFollowerFrames < _baseMasterTargetFrames * 0.5)
        {
            desiredTargetFrames += (int)(_baseMasterTargetFrames * 0.5) - minFollowerFrames;
        }

        // Initialize effective minimum if needed
        if (_effectiveMinTargetFrames < 0)
        {
            _effectiveMinTargetFrames = _baseMasterTargetFrames;
        }

        // Gradually rebuild effective minimum when no underruns
        if (_recentUnderrunCount == 0 && _recentSampleCount > 0)
        {
            _effectiveMinTargetFrames = (_effectiveMinTargetFrames * (1.0 - MinTargetRebuildAlpha)) + (_baseMasterTargetFrames * MinTargetRebuildAlpha);
        }
        _recentSampleCount++;

        desiredTargetFrames = Math.Clamp(desiredTargetFrames, (int)_effectiveMinTargetFrames, _maxMasterTargetFrames);
        
        // Single smooth convergence speed
        _adaptiveMasterTargetFrames = _adaptiveMasterTargetFrames <= 0
            ? desiredTargetFrames
            : (_adaptiveMasterTargetFrames * (1.0 - TargetSmoothingAlpha)) + (desiredTargetFrames * TargetSmoothingAlpha);
        
        _adaptiveMasterTargetFrames = Math.Clamp(_adaptiveMasterTargetFrames, (int)_effectiveMinTargetFrames, _maxMasterTargetFrames);
    }

    public void ReportUnderruns(long underrunDelta)
    {
        lock (_syncLock)
        {
            if (underrunDelta > 0)
            {
                _totalMasterUnderruns += underrunDelta;
                _recentUnderrunCount += underrunDelta;
                // When underruns are happening, lower the effective minimum target to allow
                // the system to drain buffered audio more aggressively, creating margin for
                // the next batch of audio. Once underruns stop, the minimum slowly rebuilds.
                if (_effectiveMinTargetFrames < 0)
                {
                    _effectiveMinTargetFrames = _baseMasterTargetFrames;
                }
                int underrunMinTarget = Math.Max(1, (int)(_baseMasterTargetFrames * MinTargetDuringUnderrunFraction));
                _effectiveMinTargetFrames = Math.Min(_effectiveMinTargetFrames, underrunMinTarget);
            }
            else
            {
                // No underruns in this batch: reset the counter so the minimum can rebuild.
                _recentUnderrunCount = 0;
            }
        }
    }

    public long GetTotalUnderruns()
    {
        lock (_syncLock)
        {
            return _totalMasterUnderruns;
        }
    }

    private int ComputePendingSlipNoLock(string consumerId, OutputState state)
    {
        if (state.FramesRendered < WarmupFrames) return 0;
        if (state.BufferedFrames < 0) return 0;

        if (!_states.TryGetValue(_masterConsumerId, out var masterState)) return 0;
        if (consumerId != _masterConsumerId && masterState.SmoothedBufferedFrames < 0) return 0;

        // Compute ratio correction (playback speed adjustment)
        state.Ratio = ComputeFollowerRatioNoLock(consumerId, state.SmoothedErrorFrames, state.Ratio);

        // Slip only on large errors: discard 1 frame if error is large (only slip +1, never -1)
        int slip = 0;
        if (state.SmoothedErrorFrames >= 192)  // ~4ms at 48kHz; discard to close large gaps
        {
            slip = 1;
            state.CorrectionCount += 1;
        }

        return slip;
    }

    private double ComputeFollowerRatioNoLock(string consumerId, double errorFrames, double currentRatio)
    {
        if (consumerId == _masterConsumerId)
            return 1.0;

        // Followers can only slow down (ratio 0.98-1.0), never speed up.
        // Within stable band, don't adjust. Otherwise, adjust toward 1.0 or toward slowdown.
        double targetPpm = Math.Abs(errorFrames) <= StableSettleBandFrames 
            ? 0 
            : Math.Clamp(errorFrames * RatioGainPpmPerFrame, -2400, 0);  // Clamp to slow-down only

        double targetRatio = Math.Clamp(1.0 + (targetPpm / 1_000_000.0), 0.98, 1.0);
        
        if (currentRatio <= 0)
            return targetRatio;

        return (currentRatio * (1.0 - RatioSmoothingAlpha)) + (targetRatio * RatioSmoothingAlpha);
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
    private int _deviceDelayMs;
    private int _outputBufferMs;
    private long _underrunCount;
    private readonly float[] _peakLevels;

    public record struct CaptureSource(RingBuffer Buffer, int GlobalChannelOffset, int Channels);

    public MixingSampleProvider(
        RoutingMatrix matrix,
        List<CaptureSource> sources,
        int outputChannelOffset,
        int outputChannels,
        int sampleRate,
        int outputDelayMs,
        int outputBufferMs,
        double outputBaseLatencyMs,
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
        _peakLevels = new float[outputChannels];
        _syncCoordinator.RegisterConsumer(_consumerId);
        _deviceDelayMs = Math.Clamp(outputDelayMs, 0, 5000);
        _outputBufferMs = Math.Clamp(outputBufferMs, 5, 200);
        RebuildDelayBuffer();
    }

    public WaveFormat WaveFormat => _waveFormat;
    public long UnderrunCount => Interlocked.Read(ref _underrunCount);
    public long SyncCorrectionCount => _syncCoordinator.GetCorrectionCount(_consumerId);
    public long DroppedFrames => GetDroppedFramesForConsumer();
    public double OutputMovingAverageMs => _sampleRate > 0
        ? Math.Round((_syncCoordinator.GetConsumerTargetFrames(_consumerId) * 1000.0) / _sampleRate, 1)
        : 0;
    public double OutputVariationRangeMs => _sampleRate > 0
        ? Math.Round((_syncCoordinator.GetConsumerVariationRangeFrames(_consumerId) * 1000.0) / _sampleRate, 1)
        : 0;

    /// <summary>
    /// Returns a snapshot of per-output-channel peak levels (0..1) without resetting.
    /// </summary>
    public float[] PeekPeakLevels()
    {
        var snapshot = new float[_peakLevels.Length];
        for (int i = 0; i < _peakLevels.Length; i++)
        {
            snapshot[i] = _peakLevels[i];
        }
        return snapshot;
    }

    /// <summary>
    /// Returns a snapshot of per-output-channel peak levels (0..1) and resets the running peaks.
    /// </summary>
    public float[] SamplePeakLevels()
    {
        var snapshot = new float[_peakLevels.Length];
        for (int i = 0; i < _peakLevels.Length; i++)
        {
            snapshot[i] = _peakLevels[i];
            _peakLevels[i] = 0f;
        }
        return snapshot;
    }

    public void SetDeviceDelayMs(int delayMs)
    {
        _deviceDelayMs = Math.Clamp(delayMs, 0, 5000);
        RebuildDelayBuffer();
    }

    public void SetOutputBufferMs(int bufferMs)
    {
        _outputBufferMs = Math.Clamp(bufferMs, 5, 200);
        RebuildDelayBuffer();
    }

    private void RebuildDelayBuffer()
    {
        var totalDelayMs = _deviceDelayMs + _outputBufferMs;
        var delayFrames = Math.Clamp((int)Math.Round(_sampleRate * (totalDelayMs / 1000.0)), 0, _sampleRate * 5);
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

        long underrunsAtStart = UnderrunCount;

        int bufferedFrames = GetBufferedFramesForConsumer();
        _syncCoordinator.ReportBufferedFrames(_consumerId, bufferedFrames);
        int slip = _syncCoordinator.ConsumeFrameSlip(_consumerId);
        double ratio = _syncCoordinator.GetConsumerRatio(_consumerId);
        int sourceFrames = Math.Max(1, (int)Math.Round(frames * ratio) + slip);

        int sourceSamples = sourceFrames * _outputChannels;
        if (_mixBuffer.Length < sourceSamples)
            _mixBuffer = new float[sourceSamples];

        Array.Clear(_mixBuffer, 0, sourceSamples);

        var front = _matrix.GetFrontBuffer();
        int matOutCh = _matrix.OutputChannels;
        float muteLinear = _matrix.TransientMuteAll ? 0f : 1f;

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

                        float sample = _sourceTempBuffer[f * src.Channels + srcCh] * cp.Gain * muteLinear;
                        _mixBuffer[f * _outputChannels + dstCh] += sample;
                    }
                }
            }

            // Consume the frames we read
            src.Buffer.ReadForConsumer(_consumerId, _sourceTempBuffer, 0, framesRead);
        }

        FitMixedFramesToOutput(buffer, offset, frames, sourceFrames);

        ApplyOutputDelay(buffer, offset, count);

        // Per-channel peak after delay (what actually leaves the device).
        for (int f = 0; f < frames; f++)
        {
            int baseIdx = offset + f * _outputChannels;
            for (int c = 0; c < _outputChannels; c++)
            {
                float v = buffer[baseIdx + c];
                if (v < 0) v = -v;
                if (v > _peakLevels[c]) _peakLevels[c] = v;
            }
        }

        _syncCoordinator.OnFramesRendered(_consumerId, frames);

        // Report any new underruns to the sync coordinator so it can raise the target buffer.
        long underrunsAtEnd = UnderrunCount;
        long underrunDelta = underrunsAtEnd - underrunsAtStart;
        if (underrunDelta > 0)
        {
            _syncCoordinator.ReportUnderruns(underrunDelta);
        }

        return count;
    }

    private void FitMixedFramesToOutput(float[] output, int outputOffset, int outputFrames, int sourceFrames)
    {
        if (sourceFrames <= 0 || outputFrames <= 0)
        {
            return;
        }

        if (sourceFrames == outputFrames)
        {
            for (int i = 0; i < outputFrames * _outputChannels; i++)
            {
                output[outputOffset + i] = ClampSample(_mixBuffer[i]);
            }
            return;
        }

        // Smoothly fit sourceFrames to outputFrames to avoid abrupt frame insert/drop artifacts.
        double maxSrcPos = Math.Max(0, sourceFrames - 1);
        double step = outputFrames > 1 ? maxSrcPos / (outputFrames - 1) : 0;
        for (int outFrame = 0; outFrame < outputFrames; outFrame++)
        {
            double srcPos = step * outFrame;
            int srcLo = (int)srcPos;
            int srcHi = Math.Min(srcLo + 1, sourceFrames - 1);
            double frac = srcPos - srcLo;

            int outBase = outputOffset + outFrame * _outputChannels;
            int srcLoBase = srcLo * _outputChannels;
            int srcHiBase = srcHi * _outputChannels;
            for (int ch = 0; ch < _outputChannels; ch++)
            {
                float lo = _mixBuffer[srcLoBase + ch];
                float hi = _mixBuffer[srcHiBase + ch];
                float value = (float)(lo + ((hi - lo) * frac));
                output[outBase + ch] = ClampSample(value);
            }
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

    private int GetBufferedFramesForConsumer()
    {
        if (_sources.Count == 0) return 0;

        int minBufferedFrames = int.MaxValue;
        foreach (var source in _sources)
        {
            int availableFrames = source.Buffer.GetAvailableFrames(_consumerId);
            minBufferedFrames = Math.Min(minBufferedFrames, availableFrames);
        }

        return minBufferedFrames == int.MaxValue ? 0 : minBufferedFrames;
    }

    private long GetDroppedFramesForConsumer()
    {
        if (_sources.Count == 0) return 0;

        long dropped = 0;
        foreach (var source in _sources)
        {
            dropped += source.Buffer.GetDroppedFramesForConsumer(_consumerId);
        }

        return dropped;
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

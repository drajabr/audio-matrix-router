using NAudio.Wave;
using System.Threading;

namespace AudioMatrixRouter.Audio;

public sealed class OutputSyncCoordinator
{
    private const int DriftThresholdFrames = 96;
    private const int CorrectionCooldownFrames = 4096;
    private const int WarmupFrames = 48000;
    private const double DriftSmoothingAlpha = 0.12;
    private const double TargetSmoothingAlphaSlow = 0.0035;
    private const double TargetSmoothingAlphaRescue = 0.09;
    private const double BufferedFramesSmoothingAlpha = 0.1;
    private const double StableBiasLearningAlpha = 0.0015;
    private const double ErrorSmoothingAlpha = 0.12;
    private const int StableSettleBandFrames = 32;
    private const double DirectionFlipThresholdScale = 1.35;
    private const int CoarseSlipThresholdFrames = DriftThresholdFrames * 2;
    private const double RatioGainPpmPerFrame = 8.0;
    private const double MaxRatioPpm = 2400.0;
    private const double RatioSmoothingAlpha = 0.25;
    private const int StableCyclesBeforeBiasLearning = 1500;
    private const int StableCyclesDecayStep = 8;
    private const double VariationRangeDecayAlpha = 0.003;
    // Spike rejection base: ~5 ms at 48 kHz; close enough for 44.1 kHz as well.
    private const int SpikeRejectThresholdFramesBase = 240;
    private const int SpikeRejectThresholdFramesMax = 960; // ~20 ms, max adaptive threshold
    private const double SpikeThresholdGrowthAlpha = 0.01; // Slow growth when spikes are frequent
    private const double SpikeThresholdDecayAlpha = 0.002; // Slow decay when spikes are rare
    private const int UnderrrunRescueThresholdFrames = 100; // Trigger rescue when underruns occur

    private readonly object _syncLock = new();
    private readonly Dictionary<string, OutputState> _states = new(StringComparer.Ordinal);
    private string _masterConsumerId;
    private int _baseMasterTargetFrames;
    private int _maxMasterTargetFrames;
    private double _adaptiveMasterTargetFrames;
    private double _adaptiveSpikeRejectThresholdFrames = SpikeRejectThresholdFramesBase;
    private int _recentSpikeCount = 0;
    private int _recentNormalCount = 0;
    private long _totalMasterUnderruns = 0;

    private sealed class OutputState
    {
        public long FramesRendered;
        public double Ratio = 1.0;
        public int PendingFrameSlip;
        public long NextCorrectionAt;
        public long CorrectionCount;
        public double SmoothedAbsDriftFrames;
        public int BufferedFrames = -1;
        public double SmoothedBufferedFrames = -1;
        public double LearnedBiasFrames;
        public double SmoothedErrorFrames;
        public int LastSlipDirection;
        public int StableCycles;
        public double RollingMinBufferedFrames = -1;
        public double RollingMaxBufferedFrames = -1;
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
                state.NextCorrectionAt = 0;
                state.SmoothedAbsDriftFrames = 0;
                state.BufferedFrames = -1;
                state.SmoothedBufferedFrames = -1;
                state.LearnedBiasFrames = 0;
                state.SmoothedErrorFrames = 0;
                state.LastSlipDirection = 0;
                state.StableCycles = 0;
                state.RollingMinBufferedFrames = -1;
                state.RollingMaxBufferedFrames = -1;
            }

            _adaptiveMasterTargetFrames = _baseMasterTargetFrames;
        }
    }

    public void SetMasterBufferTarget(int baseMasterTargetFrames, int maxMasterTargetFrames)
    {
        lock (_syncLock)
        {
            _baseMasterTargetFrames = Math.Max(1, baseMasterTargetFrames);
            _maxMasterTargetFrames = Math.Max(_baseMasterTargetFrames, maxMasterTargetFrames);
            _adaptiveMasterTargetFrames = Math.Clamp(_adaptiveMasterTargetFrames, _baseMasterTargetFrames, _maxMasterTargetFrames);
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

    public void SetConsumerLearnedBiasFrames(string consumerId, double learnedBiasFrames)
    {
        lock (_syncLock)
        {
            if (!_states.TryGetValue(consumerId, out var state)) return;

            state.LearnedBiasFrames = learnedBiasFrames;
            state.SmoothedErrorFrames = 0;
            state.LastSlipDirection = 0;
            state.StableCycles = 0;
            state.RollingMinBufferedFrames = -1;
            state.RollingMaxBufferedFrames = -1;
        }
    }

    public double GetConsumerLearnedBiasFrames(string consumerId)
    {
        lock (_syncLock)
        {
            return _states.TryGetValue(consumerId, out var state)
                ? state.LearnedBiasFrames
                : 0;
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

            // Spike rejection: once the slow EMA is established, skip samples that deviate
            // beyond the adaptive threshold so periodic device bursts don't pollute the error signal.
            // The threshold adapts: grows when spikes are frequent (so the EMA learns the device pattern),
            // decays when spikes are rare.
            bool isSpike = state.SmoothedBufferedFrames >= 0
                && state.FramesRendered >= WarmupFrames
                && Math.Abs(state.BufferedFrames - state.SmoothedBufferedFrames) > _adaptiveSpikeRejectThresholdFrames;

            if (!isSpike)
            {
                state.SmoothedBufferedFrames = state.SmoothedBufferedFrames < 0
                    ? state.BufferedFrames
                    : (state.SmoothedBufferedFrames * (1.0 - BufferedFramesSmoothingAlpha)) + (state.BufferedFrames * BufferedFramesSmoothingAlpha);
                _recentNormalCount++;
            }
            else
            {
                _recentSpikeCount++;
            }

            // Adapt the spike threshold: if spikes are common, raise the threshold so the EMA
            // gradually learns the actual device delivery rate. If spikes are rare, lower it.
            if (_recentSpikeCount + _recentNormalCount >= 100)
            {
                double spikeRate = _recentSpikeCount / (double)(_recentSpikeCount + _recentNormalCount);
                double targetThreshold = spikeRate > 0.05 // If >5% of samples are spikes
                    ? _adaptiveSpikeRejectThresholdFrames * (1.0 + SpikeThresholdGrowthAlpha)
                    : _adaptiveSpikeRejectThresholdFrames * (1.0 - SpikeThresholdDecayAlpha);
                _adaptiveSpikeRejectThresholdFrames = Math.Clamp(targetThreshold, SpikeRejectThresholdFramesBase, SpikeRejectThresholdFramesMax);
                _recentSpikeCount = 0;
                _recentNormalCount = 0;
            }

            UpdateVariationBandNoLock(state);
            RecomputeAdaptiveMasterTargetNoLock();

            if (!_states.TryGetValue(_masterConsumerId, out var masterState)) return;
            if (consumerId == _masterConsumerId)
            {
                state.SmoothedAbsDriftFrames = 0;
                double masterTargetError = state.SmoothedBufferedFrames - _adaptiveMasterTargetFrames;
                state.SmoothedErrorFrames = state.SmoothedErrorFrames == 0
                    ? masterTargetError
                    : (state.SmoothedErrorFrames * (1.0 - ErrorSmoothingAlpha)) + (masterTargetError * ErrorSmoothingAlpha);
                if (Math.Abs(state.SmoothedErrorFrames) <= StableSettleBandFrames)
                {
                    state.LastSlipDirection = 0;
                }
                state.StableCycles = 0;
                return;
            }

            if (state.BufferedFrames < 0 || masterState.BufferedFrames < 0 || masterState.SmoothedBufferedFrames < 0)
            {
                state.SmoothedAbsDriftFrames = 0;
                return;
            }

            double rawFollowerErrorFrames = state.SmoothedBufferedFrames - masterState.SmoothedBufferedFrames;
            if (Math.Abs(rawFollowerErrorFrames) <= StableSettleBandFrames)
            {
                state.StableCycles++;
                if (state.StableCycles >= StableCyclesBeforeBiasLearning)
                {
                    state.LearnedBiasFrames = (state.LearnedBiasFrames * (1.0 - StableBiasLearningAlpha)) + (rawFollowerErrorFrames * StableBiasLearningAlpha);
                }
            }
            else
            {
                state.StableCycles = Math.Max(0, state.StableCycles - StableCyclesDecayStep);
            }

            double targetBufferedFrames = masterState.SmoothedBufferedFrames + state.LearnedBiasFrames;
            double errorFrames = state.SmoothedBufferedFrames - targetBufferedFrames;
            state.SmoothedErrorFrames = state.SmoothedErrorFrames == 0
                ? errorFrames
                : (state.SmoothedErrorFrames * (1.0 - ErrorSmoothingAlpha)) + (errorFrames * ErrorSmoothingAlpha);

            double absErrorFrames = Math.Abs(state.SmoothedErrorFrames);
            state.SmoothedAbsDriftFrames = state.SmoothedAbsDriftFrames <= 0
                ? absErrorFrames
                : (state.SmoothedAbsDriftFrames * (1.0 - DriftSmoothingAlpha)) + (absErrorFrames * DriftSmoothingAlpha);

            if (Math.Abs(state.SmoothedErrorFrames) <= StableSettleBandFrames)
            {
                state.LastSlipDirection = 0;
            }
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

    public double GetSmoothedAbsDriftFrames(string consumerId)
    {
        lock (_syncLock)
        {
            return _states.TryGetValue(consumerId, out var state)
                ? state.SmoothedAbsDriftFrames
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

            if (_states.TryGetValue(_masterConsumerId, out var masterState))
            {
                var masterFrames = masterState.SmoothedBufferedFrames >= 0
                    ? masterState.SmoothedBufferedFrames
                    : masterState.BufferedFrames;
                return Math.Max(0, masterFrames + state.LearnedBiasFrames);
            }

            return state.SmoothedBufferedFrames >= 0 ? state.SmoothedBufferedFrames : Math.Max(0, state.BufferedFrames);
        }
    }

    public double GetConsumerVariationRangeFrames(string consumerId)
    {
        lock (_syncLock)
        {
            if (!_states.TryGetValue(consumerId, out var state)) return 0;
            if (state.RollingMinBufferedFrames < 0 || state.RollingMaxBufferedFrames < 0) return 0;
            return Math.Max(0, state.RollingMaxBufferedFrames - state.RollingMinBufferedFrames);
        }
    }

    private void UpdateVariationBandNoLock(OutputState state)
    {
        if (state.SmoothedBufferedFrames < 0) return;

        if (state.RollingMinBufferedFrames < 0 || state.RollingMaxBufferedFrames < 0)
        {
            state.RollingMinBufferedFrames = state.SmoothedBufferedFrames;
            state.RollingMaxBufferedFrames = state.SmoothedBufferedFrames;
            return;
        }

        state.RollingMinBufferedFrames = Math.Min(state.RollingMinBufferedFrames, state.SmoothedBufferedFrames);
        state.RollingMaxBufferedFrames = Math.Max(state.RollingMaxBufferedFrames, state.SmoothedBufferedFrames);

        if (state.SmoothedBufferedFrames > state.RollingMinBufferedFrames)
        {
            state.RollingMinBufferedFrames += (state.SmoothedBufferedFrames - state.RollingMinBufferedFrames) * VariationRangeDecayAlpha;
        }
        if (state.SmoothedBufferedFrames < state.RollingMaxBufferedFrames)
        {
            state.RollingMaxBufferedFrames += (state.SmoothedBufferedFrames - state.RollingMaxBufferedFrames) * VariationRangeDecayAlpha;
        }
    }

    private void RecomputeAdaptiveMasterTargetNoLock()
    {
        int desiredTargetFrames = _baseMasterTargetFrames;
        int weakestFollowerBufferedFrames = int.MaxValue;
        bool hasFollowerBufferedState = false;

        foreach (var pair in _states)
        {
            if (pair.Key == _masterConsumerId) continue;
            if (pair.Value.BufferedFrames < 0) continue;

            weakestFollowerBufferedFrames = Math.Min(weakestFollowerBufferedFrames, pair.Value.BufferedFrames);
            hasFollowerBufferedState = true;
        }

        if (hasFollowerBufferedState)
        {
            int lowWatermarkFrames = Math.Max(DriftThresholdFrames * 2, (int)Math.Round(_baseMasterTargetFrames * 0.72));
            if (weakestFollowerBufferedFrames < lowWatermarkFrames)
            {
                desiredTargetFrames += lowWatermarkFrames - weakestFollowerBufferedFrames;
            }
        }

        // Spike absorption: if the master's raw buffer jumps well above the adaptive target,
        // raise the target quickly to absorb the spike rather than issuing slip corrections.
        if (_states.TryGetValue(_masterConsumerId, out var masterStateForSpike)
            && masterStateForSpike.BufferedFrames >= 0
            && masterStateForSpike.FramesRendered >= WarmupFrames)
        {
            int masterExcess = masterStateForSpike.BufferedFrames - (int)_adaptiveMasterTargetFrames;
            if (masterExcess > _adaptiveSpikeRejectThresholdFrames)
            {
                desiredTargetFrames = Math.Clamp(
                    (int)_adaptiveMasterTargetFrames + masterExcess,
                    _baseMasterTargetFrames,
                    _maxMasterTargetFrames);
            }
        }

        desiredTargetFrames = Math.Clamp(desiredTargetFrames, _baseMasterTargetFrames, _maxMasterTargetFrames);
        bool rescueMode = desiredTargetFrames > (_adaptiveMasterTargetFrames + StableSettleBandFrames);
        double alpha = rescueMode ? TargetSmoothingAlphaRescue : TargetSmoothingAlphaSlow;
        _adaptiveMasterTargetFrames = _adaptiveMasterTargetFrames <= 0
            ? desiredTargetFrames
            : (_adaptiveMasterTargetFrames * (1.0 - alpha)) + (desiredTargetFrames * alpha);
        _adaptiveMasterTargetFrames = Math.Clamp(_adaptiveMasterTargetFrames, _baseMasterTargetFrames, _maxMasterTargetFrames);
    }

    public void ReportUnderruns(long underrunDelta)
    {
        lock (_syncLock)
        {
            if (underrunDelta > 0)
            {
                _totalMasterUnderruns += underrunDelta;
                // Underruns indicate buffer starvation; trigger immediate rescue mode.
                int desiredTargetFrames = Math.Clamp(
                    (int)_adaptiveMasterTargetFrames + UnderrrunRescueThresholdFrames,
                    _baseMasterTargetFrames,
                    _maxMasterTargetFrames);
                // Fast move to the higher target using rescue alpha.
                _adaptiveMasterTargetFrames = (_adaptiveMasterTargetFrames * (1.0 - TargetSmoothingAlphaRescue)) + (desiredTargetFrames * TargetSmoothingAlphaRescue);
                _adaptiveMasterTargetFrames = Math.Clamp(_adaptiveMasterTargetFrames, _baseMasterTargetFrames, _maxMasterTargetFrames);
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
        if (state.FramesRendered < state.NextCorrectionAt) return 0;
        if (state.BufferedFrames < 0) return 0;

        if (!_states.TryGetValue(_masterConsumerId, out var masterState)) return 0;
        if (consumerId != _masterConsumerId && masterState.BufferedFrames < 0) return 0;

        double currentBufferedFrames = state.SmoothedBufferedFrames >= 0
            ? state.SmoothedBufferedFrames
            : state.BufferedFrames;

        double targetBufferedFrames = consumerId == _masterConsumerId
            ? _adaptiveMasterTargetFrames
            : ((masterState.SmoothedBufferedFrames >= 0 ? masterState.SmoothedBufferedFrames : masterState.BufferedFrames) + state.LearnedBiasFrames);

        double errorFrames = state.SmoothedErrorFrames == 0
            ? currentBufferedFrames - targetBufferedFrames
            : state.SmoothedErrorFrames;

        state.Ratio = ComputeFollowerRatioNoLock(consumerId, errorFrames, state.Ratio);

        double activeThresholdFrames = DriftThresholdFrames;
        int desiredDirection = errorFrames >= 0 ? 1 : -1;
        if (state.LastSlipDirection != 0 && desiredDirection != state.LastSlipDirection)
        {
            activeThresholdFrames *= DirectionFlipThresholdScale;
        }

        activeThresholdFrames = Math.Max(activeThresholdFrames, CoarseSlipThresholdFrames);

        int slip = 0;
        if (errorFrames >= activeThresholdFrames)
        {
            // Too much queued audio: consume one extra frame to close the gap.
            slip = 1;
        }
        else if (errorFrames <= -activeThresholdFrames)
        {
            // Too little queued audio: consume one fewer frame so the queue can rebuild.
            slip = -1;
        }

        if (slip != 0)
        {
            state.NextCorrectionAt = state.FramesRendered + CorrectionCooldownFrames;
            state.CorrectionCount += 1;
            state.LastSlipDirection = slip;
        }

        return slip;
    }

    private double ComputeFollowerRatioNoLock(string consumerId, double errorFrames, double currentRatio)
    {
        if (consumerId == _masterConsumerId)
        {
            return 1.0;
        }

        double targetPpm = Math.Clamp(errorFrames * RatioGainPpmPerFrame, -MaxRatioPpm, MaxRatioPpm);
        if (Math.Abs(errorFrames) <= StableSettleBandFrames)
        {
            targetPpm = 0;
        }

        double targetRatio = 1.0 + (targetPpm / 1_000_000.0);
        if (currentRatio <= 0)
        {
            return targetRatio;
        }

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
        SetOutputBaseLatencyMs(outputBaseLatencyMs);
        _deviceDelayMs = Math.Clamp(outputDelayMs, 0, 5000);
        _outputBufferMs = Math.Clamp(outputBufferMs, 5, 200);
        RebuildDelayBuffer();
    }

    public WaveFormat WaveFormat => _waveFormat;
    public long UnderrunCount => Interlocked.Read(ref _underrunCount);
    public long SyncCorrectionCount => _syncCoordinator.GetCorrectionCount(_consumerId);
    public long DroppedFrames => GetDroppedFramesForConsumer();
    public double OutputBaseLatencyMs => _sampleRate > 0
        ? Math.Round((_syncCoordinator.GetConsumerLearnedBiasFrames(_consumerId) * 1000.0) / _sampleRate, 2)
        : 0;
    public double OutputMovingAverageMs => _sampleRate > 0
        ? Math.Round((_syncCoordinator.GetConsumerTargetFrames(_consumerId) * 1000.0) / _sampleRate, 1)
        : 0;
    public double OutputVariationRangeMs => _sampleRate > 0
        ? Math.Round((_syncCoordinator.GetConsumerVariationRangeFrames(_consumerId) * 1000.0) / _sampleRate, 1)
        : 0;
    public double OutputJitterMs => _sampleRate > 0
        ? Math.Round((_syncCoordinator.GetSmoothedAbsDriftFrames(_consumerId) * 1000.0) / _sampleRate, 1)
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

    public void SetOutputBaseLatencyMs(double baseLatencyMs)
    {
        double biasFrames = _sampleRate > 0
            ? (baseLatencyMs / 1000.0) * _sampleRate
            : 0;
        _syncCoordinator.SetConsumerLearnedBiasFrames(_consumerId, biasFrames);
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

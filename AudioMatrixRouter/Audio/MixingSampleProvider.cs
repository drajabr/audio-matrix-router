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
    private const double TargetSmoothingRiseAlpha = 0.008;      // Raise target conservatively to avoid abrupt latency jumps
    private const double TargetSmoothingFallAlpha = 0.04;       // Drop target faster so runtime latency returns near floor quickly
    
    // ===== Follower Sync =====
    private const double RatioGainPpmPerFrame = 2.0;            // PPM adjustment per frame of error
    private const double RatioSmoothingAlpha = 0.08;            // EMA for playback speed correction
    private const int StableSettleBandFrames = 16;              // Don't correct tiny errors (~0.3ms at 48kHz)
    private const double MaxFollowerRatioPpm = 2400;            // Limit speed correction to +/-2400 ppm
    private const int SlipThresholdFrames = 192;

    // ===== Fast Catch-Up Mode =====
    private const int FastCatchUpEnterErrorFrames = 64;         // Enter when lag is clearly visible
    private const int FastCatchUpExitErrorFrames = 16;          // Exit only after tight re-lock
    private const int FastCatchUpEnterConfirmBlocks = 3;        // Require persistence before entering
    private const int FastCatchUpMinHoldBlocks = 8;             // Stay long enough to actually recover
    private const double FastCatchUpRatioGainPpmPerFrame = 6.0; // Aggressive, bounded catch-up gain
    private const double FastCatchUpMaxFollowerRatioPpm = 9000; // Temporary higher correction ceiling
    private const int FastCatchUpSlipThresholdFrames = 96;      // Allow earlier slips while recovering

    // ===== Guardrails =====
    private const int CorrectionCooldownBlocks = 4;             // Brief pause between slips to avoid back-to-back oscillation
    private const int PostRecoveryUnderrunWindowBlocks = 80;    // Track underruns shortly after recovery
    
    // ===== Spike Rejection =====
    private const int SpikeRejectThresholdFramesBase = 240;     // ~5ms spike threshold
    private const double SpikeBlendAlpha = 0.005;               // Slowly learn spike pattern
    
    // ===== Underrun Recovery =====
    private const double MinTargetDuringUnderrunFraction = 0.5; // Drop floor to 50% during underruns
    private const double MinTargetRebuildAlpha = 0.01;          // Rebuild floor toward base faster when healthy
    private const double InputStarvationBoostDecay = 0.96;      // Let starvation boost decay quickly once starvation ends

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
    private double _inputStarvationBoostFrames = 0;

    private sealed class OutputState
    {
        public long FramesRendered;
        public double Ratio = 1.0;
        public int PendingFrameSlip;
        public long CorrectionCount;
        public int CorrectionCooldownBlocks;
        public int BufferedFrames = -1;
        public double SmoothedBufferedFrames = -1;
        public double SmoothedErrorFrames;
        public bool FastCatchUpActive;
        public int FastCatchUpEnterConfirmBlocks;
        public int FastCatchUpHoldBlocks;
        public long FastCatchUpFrames;
        public long LastRateSampleFrames;
        public long LastRateSampleCorrections;
        public double CorrectionRatePerKFrames;
        public long LastObservedFrames;
        public int PostRecoveryWindowBlocksRemaining;
        public long PostRecoveryUnderruns;
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
                state.FramesRendered = 0;
                state.PendingFrameSlip = 0;
                state.BufferedFrames = -1;
                state.SmoothedBufferedFrames = -1;
                state.SmoothedErrorFrames = 0;
                state.CorrectionCount = 0;
                state.Ratio = 1.0;
                state.CorrectionCooldownBlocks = 0;
                state.FastCatchUpActive = false;
                state.FastCatchUpEnterConfirmBlocks = 0;
                state.FastCatchUpHoldBlocks = 0;
                state.FastCatchUpFrames = 0;
                state.LastRateSampleFrames = 0;
                state.LastRateSampleCorrections = 0;
                state.CorrectionRatePerKFrames = 0;
                state.LastObservedFrames = 0;
                state.PostRecoveryWindowBlocksRemaining = 0;
                state.PostRecoveryUnderruns = 0;
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
                _states[consumerId] = new();
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

    public bool IsFastCatchUpActive(string consumerId)
    {
        lock (_syncLock)
        {
            return _states.TryGetValue(consumerId, out var state) && state.FastCatchUpActive;
        }
    }

    public double GetFastCatchUpDutyPercent(string consumerId)
    {
        lock (_syncLock)
        {
            if (!_states.TryGetValue(consumerId, out var state) || state.FramesRendered <= 0)
            {
                return 0;
            }

            return Math.Clamp((state.FastCatchUpFrames * 100.0) / state.FramesRendered, 0, 100);
        }
    }

    public double GetCorrectionRatePerKFrames(string consumerId)
    {
        lock (_syncLock)
        {
            return _states.TryGetValue(consumerId, out var state)
                ? Math.Max(0, state.CorrectionRatePerKFrames)
                : 0;
        }
    }

    public long GetPostRecoveryUnderruns(string consumerId)
    {
        lock (_syncLock)
        {
            return _states.TryGetValue(consumerId, out var state)
                ? state.PostRecoveryUnderruns
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

            if (!_states.TryGetValue(_masterConsumerId, out var masterState))
            {
                return 0;
            }

            double ResolveBuffered(OutputState s) => s.SmoothedBufferedFrames >= 0 ? s.SmoothedBufferedFrames : Math.Max(0, s.BufferedFrames);

            // For a follower, report current absolute spread to master.
            if (consumerId != _masterConsumerId)
            {
                return Math.Abs(ResolveBuffered(state) - ResolveBuffered(masterState));
            }

            // For master, report worst follower spread (max absolute delta to master).
            double masterBuffered = ResolveBuffered(masterState);
            double worstSpread = 0;
            foreach (var pair in _states)
            {
                if (pair.Key == _masterConsumerId) continue;
                double followerBuffered = ResolveBuffered(pair.Value);
                worstSpread = Math.Max(worstSpread, Math.Abs(followerBuffered - masterBuffered));
            }

            return worstSpread;
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

        if (_inputStarvationBoostFrames > 0)
        {
            desiredTargetFrames += (int)Math.Round(_inputStarvationBoostFrames);
            _inputStarvationBoostFrames *= InputStarvationBoostDecay;
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
        
        // Asymmetric convergence: drift down toward floor faster than drifting up.
        if (_adaptiveMasterTargetFrames <= 0)
        {
            _adaptiveMasterTargetFrames = desiredTargetFrames;
        }
        else
        {
            double alpha = desiredTargetFrames <= _adaptiveMasterTargetFrames
                ? TargetSmoothingFallAlpha
                : TargetSmoothingRiseAlpha;
            _adaptiveMasterTargetFrames = (_adaptiveMasterTargetFrames * (1.0 - alpha)) + (desiredTargetFrames * alpha);
        }
        
        _adaptiveMasterTargetFrames = Math.Clamp(_adaptiveMasterTargetFrames, (int)_effectiveMinTargetFrames, _maxMasterTargetFrames);
    }

    public void ReportInputStarvation(int missingFrames)
    {
        lock (_syncLock)
        {
            if (missingFrames <= 0)
            {
                _inputStarvationBoostFrames *= InputStarvationBoostDecay;
                return;
            }

            int maxBoost = Math.Max(0, _maxMasterTargetFrames - _baseMasterTargetFrames);
            if (maxBoost == 0)
            {
                return;
            }

            double desiredBoost = Math.Clamp(missingFrames, 0, maxBoost);
            _inputStarvationBoostFrames = Math.Max(_inputStarvationBoostFrames, desiredBoost);
        }
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

                foreach (var state in _states.Values)
                {
                    if (state.PostRecoveryWindowBlocksRemaining > 0)
                    {
                        state.PostRecoveryUnderruns += underrunDelta;
                    }
                }
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
        if (state.BufferedFrames < 0) return 0;

        if (!_states.TryGetValue(_masterConsumerId, out var masterState)) return 0;
        if (consumerId != _masterConsumerId && masterState.SmoothedBufferedFrames < 0) return 0;

        bool isMaster = consumerId == _masterConsumerId;
        if (isMaster)
        {
            state.CorrectionCooldownBlocks = 0;
            state.FastCatchUpActive = false;
            state.FastCatchUpEnterConfirmBlocks = 0;
            state.FastCatchUpHoldBlocks = 0;
            state.PostRecoveryWindowBlocksRemaining = 0;
            state.Ratio = 1.0;
            return 0;
        }

        state.CorrectionCooldownBlocks = Math.Max(0, state.CorrectionCooldownBlocks - 1);
        long observedDeltaFrames = Math.Max(0, state.FramesRendered - state.LastObservedFrames);
        if (state.FastCatchUpActive)
        {
            state.FastCatchUpFrames += observedDeltaFrames;
        }
        state.LastObservedFrames = state.FramesRendered;
        if (state.PostRecoveryWindowBlocksRemaining > 0)
        {
            state.PostRecoveryWindowBlocksRemaining -= 1;
        }

        double absErrorFrames = Math.Abs(state.SmoothedErrorFrames);
        if (!state.FastCatchUpActive)
        {
            if (absErrorFrames >= FastCatchUpEnterErrorFrames)
            {
                state.FastCatchUpEnterConfirmBlocks += 1;
                if (state.FastCatchUpEnterConfirmBlocks >= FastCatchUpEnterConfirmBlocks)
                {
                    state.FastCatchUpActive = true;
                    state.FastCatchUpHoldBlocks = 0;
                    state.FastCatchUpEnterConfirmBlocks = 0;
                }
            }
            else
            {
                state.FastCatchUpEnterConfirmBlocks = 0;
            }
        }
        else
        {
            state.FastCatchUpHoldBlocks += 1;
            if (state.FastCatchUpHoldBlocks >= FastCatchUpMinHoldBlocks && absErrorFrames <= FastCatchUpExitErrorFrames)
            {
                state.FastCatchUpActive = false;
                state.FastCatchUpHoldBlocks = 0;
                state.PostRecoveryWindowBlocksRemaining = PostRecoveryUnderrunWindowBlocks;
            }
        }

        // Compute ratio correction (playback speed adjustment)
        state.Ratio = ComputeFollowerRatioNoLock(consumerId, state.SmoothedErrorFrames, state.Ratio, state.FastCatchUpActive);

        // Slip on large residual errors after ratio correction.
        // +1 consumes one extra frame (drains follower queue), -1 consumes one less frame (builds queue).
        int slip = 0;
        int slipThreshold = state.FastCatchUpActive ? FastCatchUpSlipThresholdFrames : SlipThresholdFrames;
        if (state.SmoothedErrorFrames >= slipThreshold)  // follower has too much queued vs master
        {
            slip = 1;
        }
        else if (state.SmoothedErrorFrames <= -slipThreshold) // follower has too little queued vs master
        {
            slip = -1;
        }

        if (slip != 0)
        {
            if (state.CorrectionCooldownBlocks > 0)
            {
                slip = 0;
            }
            else
            {
                state.CorrectionCount += 1;
                state.CorrectionCooldownBlocks = CorrectionCooldownBlocks;
            }
        }

        long framesDelta = state.FramesRendered - state.LastRateSampleFrames;
        if (framesDelta >= 1024)
        {
            long correctionsDelta = state.CorrectionCount - state.LastRateSampleCorrections;
            double perKFrames = framesDelta > 0 ? (correctionsDelta * 1000.0) / framesDelta : 0;
            state.CorrectionRatePerKFrames = state.CorrectionRatePerKFrames <= 0
                ? perKFrames
                : (state.CorrectionRatePerKFrames * 0.8) + (perKFrames * 0.2);
            state.LastRateSampleFrames = state.FramesRendered;
            state.LastRateSampleCorrections = state.CorrectionCount;
        }

        return slip;
    }

    private double ComputeFollowerRatioNoLock(string consumerId, double errorFrames, double currentRatio, bool fastCatchUp)
    {
        if (consumerId == _masterConsumerId)
            return 1.0;

        // Error definition: follower minus master buffered frames.
        // Positive error means follower has more queued audio (more delayed) and should speed up (>1.0).
        // Negative error means follower has less queued audio (less delayed) and should slow down (<1.0).
        double settleBand = fastCatchUp ? 0 : StableSettleBandFrames;
        double gain = fastCatchUp ? FastCatchUpRatioGainPpmPerFrame : RatioGainPpmPerFrame;
        double maxPpm = fastCatchUp ? FastCatchUpMaxFollowerRatioPpm : MaxFollowerRatioPpm;

        double targetPpm = Math.Abs(errorFrames) <= settleBand
            ? 0 
            : Math.Clamp(errorFrames * gain, -maxPpm, maxPpm);

        double targetRatio = 1.0 + (targetPpm / 1_000_000.0);
        
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
    private const int InputSyncSettleBandFrames = 64;

    private readonly RoutingMatrix _matrix;
    private readonly List<CaptureSource> _sources;
    private readonly int _outputChannelOffset;
    private readonly int _outputChannels;
    private readonly int _sampleRate;
    private readonly string _consumerId;
    private readonly OutputSyncCoordinator _syncCoordinator;
    private readonly object _delayLock = new();
    private readonly WaveFormat _waveFormat;
    private string _inputMasterDeviceId;
    private float[] _sourceTempBuffer = [];
    private float[] _discardBuffer = [];
    private float[] _mixBuffer = [];
    private float[] _delayBuffer = [];
    private int _delayWriteIndex;
    private int _deviceDelayMs;
    private int _outputBufferMs;
    private long _underrunCount;
    private readonly float[] _peakLevels;
    private readonly Dictionary<string, long> _inputSyncDiscardedFramesByDevice = new(StringComparer.Ordinal);

    public record struct CaptureSource(string DeviceId, RingBuffer Buffer, int GlobalChannelOffset, int Channels, bool IsMasterInput);

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
        var initialInputMaster = _sources.FirstOrDefault(s => s.IsMasterInput);
        _inputMasterDeviceId = !string.IsNullOrWhiteSpace(initialInputMaster.DeviceId)
            ? initialInputMaster.DeviceId
            : (_sources.Count > 0 ? _sources[0].DeviceId : string.Empty);
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
    public bool FastCatchUpActive => _syncCoordinator.IsFastCatchUpActive(_consumerId);
    public double FastCatchUpDutyPercent => Math.Round(_syncCoordinator.GetFastCatchUpDutyPercent(_consumerId), 1);
    public double SyncCorrectionRatePerSec => _sampleRate > 0
        ? Math.Round(_syncCoordinator.GetCorrectionRatePerKFrames(_consumerId) * (_sampleRate / 1000.0), 2)
        : 0;
    public long PostRecoveryUnderruns => _syncCoordinator.GetPostRecoveryUnderruns(_consumerId);

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
    }

    public void SetInputMasterDevice(string deviceId)
    {
        _inputMasterDeviceId = deviceId ?? string.Empty;
    }

    private void RebuildDelayBuffer()
    {
        lock (_delayLock)
        {
            int delayFrames = Math.Clamp((int)Math.Round(_sampleRate * (_deviceDelayMs / 1000.0)), 0, _sampleRate * 5);
            _delayBuffer = delayFrames > 0 ? new float[delayFrames * _outputChannels] : [];
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

        // Only sources that currently route into this output should influence input-side sync alignment.
        bool IsSourceActiveForThisOutput(CaptureSource source)
        {
            for (int srcCh = 0; srcCh < source.Channels; srcCh++)
            {
                int globalInCh = source.GlobalChannelOffset + srcCh;
                for (int dstCh = 0; dstCh < _outputChannels; dstCh++)
                {
                    int globalOutCh = _outputChannelOffset + dstCh;
                    int matIdx = globalInCh * matOutCh + globalOutCh;
                    if (matIdx < 0 || matIdx >= front.Length) continue;
                    if (front[matIdx].Active) return true;
                }
            }
            return false;
        }

        var syncSources = _sources.Where(IsSourceActiveForThisOutput).ToList();
        if (syncSources.Count == 0)
        {
            // If no source is routed to this output, fall back to all sources to keep consumers advancing.
            syncSources = _sources;
        }

        int referenceBufferedFrames = 0;
        if (syncSources.Count > 0)
        {
            int minBufferedFrames = int.MaxValue;
            foreach (var src in syncSources)
            {
                int availableFrames = src.Buffer.GetAvailableFrames(_consumerId);
                if (availableFrames < minBufferedFrames)
                {
                    minBufferedFrames = availableFrames;
                }
            }

            referenceBufferedFrames = minBufferedFrames == int.MaxValue ? 0 : minBufferedFrames;
        }

        foreach (var src in _sources)
        {
            int sourceBufferedFrames = src.Buffer.GetAvailableFrames(_consumerId);
            int aheadFrames = sourceBufferedFrames - referenceBufferedFrames;
            if (aheadFrames > InputSyncSettleBandFrames)
            {
                int discardFrames = Math.Min(aheadFrames - InputSyncSettleBandFrames, sourceFrames * 4);
                if (discardFrames > 0)
                {
                    DiscardFramesForConsumer(src, discardFrames);
                    sourceBufferedFrames = Math.Max(0, sourceBufferedFrames - discardFrames);
                }
            }

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

                        float signedGain = cp.PhaseInverted ? -cp.Gain : cp.Gain;
                        float sample = _sourceTempBuffer[f * src.Channels + srcCh] * signedGain * muteLinear;
                        _mixBuffer[f * _outputChannels + dstCh] += sample;
                    }
                }
            }

            // Consume the frames we read
            src.Buffer.ReadForConsumer(_consumerId, _sourceTempBuffer, 0, framesRead);
        }

        int inputStarvationFrames = Math.Max(0, sourceFrames - referenceBufferedFrames);
        _syncCoordinator.ReportInputStarvation(inputStarvationFrames);

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

    private bool IsMasterInputSource(CaptureSource source)
    {
        if (!string.IsNullOrWhiteSpace(_inputMasterDeviceId))
        {
            return string.Equals(source.DeviceId, _inputMasterDeviceId, StringComparison.Ordinal);
        }

        return source.IsMasterInput;
    }

    private void DiscardFramesForConsumer(CaptureSource source, int frames)
    {
        if (frames <= 0)
        {
            return;
        }

        int samples = frames * source.Channels;
        if (_discardBuffer.Length < samples)
        {
            _discardBuffer = new float[samples];
        }

        // Track input sync corrections (frames discarded for synchronization)
        source.Buffer.ReadForConsumer(_consumerId, _discardBuffer, 0, frames);
        if (!string.IsNullOrWhiteSpace(source.DeviceId))
        {
            _inputSyncDiscardedFramesByDevice[source.DeviceId] = _inputSyncDiscardedFramesByDevice.TryGetValue(source.DeviceId, out var current)
                ? current + frames
                : frames;
        }
    }

    public long GetInputSyncCorrectionCount(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return 0;
        return _inputSyncDiscardedFramesByDevice.TryGetValue(deviceId, out var count) ? count : 0;
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
                float delayed = _delayBuffer[_delayWriteIndex];
                _delayBuffer[_delayWriteIndex] = buffer[offset + i];
                buffer[offset + i] = delayed;

                _delayWriteIndex++;
                if (_delayWriteIndex >= _delayBuffer.Length)
                {
                    _delayWriteIndex = 0;
                }
            }
        }
    }
}

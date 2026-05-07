using NAudio.Dsp;
using NAudio.Wave;
using System.Threading;

namespace AudioMatrixRouter.Audio;

public sealed class OutputSyncCoordinator
{
    // ===== Follower Sync =====
    private const int StableSettleBandFrames = 2;               // ~0.04ms deadband — hardware-clock signal is clean enough
    private const double MaxFollowerRatioPpm = 3000;            // More headroom for continuous drift correction

    // ===== Fast Catch-Up Mode =====
    private const int FastCatchUpEnterErrorFrames = 30;         // Enter recovery sooner on real spikes
    private const int FastCatchUpExitErrorFrames = 8;           // Exit only after very tight re-lock
    private const int FastCatchUpEnterConfirmBlocks = 1;        // Spike recovery should react immediately
    private const int FastCatchUpMinHoldBlocks = 8;             // Keep recovery short but sufficient
    private const double FastCatchUpMaxFollowerRatioPpm = 6200; // Stronger temporary correction during spike recovery

    // ===== Guardrails =====
    private const int PostRecoveryUnderrunWindowBlocks = 80;    // Track underruns shortly after recovery
    
    // ===== Ratio Dynamics =====
    private const double RatioStepLimitPpmPerBlock = 260;       // Allow faster ratio movement while staying bounded
    private const double FastCatchUpRatioStepLimitPpmPerBlock = 700;

    // ===== Master Self-Correction =====
    // Master output is no longer hardcoded to ratio 1.0. It runs its own (gentler) PI loop on
    // (smoothedBuffered - adaptiveTarget) so it can SLOW DOWN when the input ring is draining
    // and SPEED UP when it's filling. Without this, a master output whose hardware clock runs
    // faster than the input clock will steadily drain the ring no matter how big the buffer is.
    // Followers continue to track the master via (follower.buf - master.buf).
    private const double MasterRatioKpPpmPerFrame = 0.6;        // Much gentler than follower (2.5)
    private const double MasterRatioKiPpmPerIntegralFrame = 0.025;
    private const double MasterMaxRatioPpm = 1500;              // Bounded ±0.15% — enough for any real crystal mismatch
    private const int MasterSettleBandFrames = 8;               // Wider deadband — master only fights real drift, not jitter

    // ===== Ratio Controller (PI-style) =====
    private const double RatioKpPpmPerFrame = 2.5;              // Stronger proportional response in normal mode
    private const double RatioKiPpmPerIntegralFrame = 0.10;     // Integral pull toward zero spread
    private const double FastCatchUpRatioKpPpmPerFrame = 3.5;   // Stronger proportional response in catch-up mode
    private const double FastCatchUpRatioKiPpmPerIntegralFrame = 0.16;
    private const double RatioIntegralClampFrames = 3000;       // Bound integral state
    
    // ===== Underrun Recovery =====
    private const double MinTargetDuringUnderrunFraction = 0.9; // Allow only small temporary dip below user buffer during real underruns
    private const double InputStarvationBoostDecay = 0.96;      // Let starvation boost decay quickly once starvation ends

    private readonly object _syncLock = new();
    private readonly Dictionary<string, OutputState> _states = new(StringComparer.Ordinal);
    private string _masterConsumerId;
    private int _baseMasterTargetFrames;
    private int _maxMasterTargetFrames;
    private double _adaptiveMasterTargetFrames;
    private long _totalMasterUnderruns = 0;
    private long _recentUnderrunCount = 0;
    private double _effectiveMinTargetFrames = -1; // -1 means uninitialized
    private double _inputStarvationBoostFrames = 0;
    private bool _globalRefillHoldActive = true;

    private sealed class OutputState
    {
        public long FramesRendered;
        public int HoldTargetFrames;
        public bool HasActiveRoutes;
        public int LastPreparedSourceFrames;
        public double Ratio = 1.0;
        public int BufferedFrames = -1;
        public double SmoothedErrorFrames;
        public bool FastCatchUpActive;
        public int FastCatchUpEnterConfirmBlocks;
        public int FastCatchUpHoldBlocks;
        public long FastCatchUpFrames;
        public long LastObservedFrames;
        public int PostRecoveryWindowBlocksRemaining;
        public long PostRecoveryUnderruns;
        public double IntegralErrorFrames;
        public double LastAppliedPpm;
        public bool PiArmed;                   // Set true once buffered crosses fill-threshold; gates PI engagement
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
                state.BufferedFrames = -1;
                state.HasActiveRoutes = false;
                state.LastPreparedSourceFrames = 0;
                state.SmoothedErrorFrames = 0;
                state.Ratio = 1.0;
                state.FastCatchUpActive = false;
                state.FastCatchUpEnterConfirmBlocks = 0;
                state.FastCatchUpHoldBlocks = 0;
                state.FastCatchUpFrames = 0;
                state.LastObservedFrames = 0;
                state.PostRecoveryWindowBlocksRemaining = 0;
                state.PostRecoveryUnderruns = 0;
                state.IntegralErrorFrames = 0;
                state.LastAppliedPpm = 0;
                state.PiArmed = false;
            }

            _adaptiveMasterTargetFrames = _baseMasterTargetFrames;
            _effectiveMinTargetFrames = _baseMasterTargetFrames;
            _recentUnderrunCount = 0;
            _globalRefillHoldActive = true;
        }
    }

    public void SetMasterBufferTarget(int baseMasterTargetFrames, int maxMasterTargetFrames)
    {
        lock (_syncLock)
        {
            _baseMasterTargetFrames = Math.Max(1, baseMasterTargetFrames);
            _maxMasterTargetFrames = Math.Max(_baseMasterTargetFrames, maxMasterTargetFrames);
            _adaptiveMasterTargetFrames = Math.Clamp(_adaptiveMasterTargetFrames, _baseMasterTargetFrames, _maxMasterTargetFrames);
            // Apply user-selected output buffer floor immediately on changes/restart.
            _effectiveMinTargetFrames = _baseMasterTargetFrames;
            _recentUnderrunCount = 0;
        }
    }

    public string GetMasterConsumerId() => _masterConsumerId;

    public List<string> GetNonMasterConsumerIds()
    {
        lock (_syncLock)
        {
            var list = new List<string>(_states.Count);
            foreach (var key in _states.Keys)
                if (key != _masterConsumerId) list.Add(key);
            return list;
        }
    }

    public void RegisterConsumer(string consumerId, int sampleRate = 0, int outputBufferMs = 0)
    {
        lock (_syncLock)
        {
            if (!_states.ContainsKey(consumerId))
            {
                _states[consumerId] = new OutputState
                {
                    HoldTargetFrames = sampleRate > 0
                        ? Math.Max(1, (int)Math.Round(sampleRate * (Math.Max(1, outputBufferMs) / 1000.0)))
                        : 0
                };
            }
            else if (_states.TryGetValue(consumerId, out var state))
            {
                if (sampleRate > 0)
                {
                    state.HoldTargetFrames = Math.Max(1, (int)Math.Round(sampleRate * (Math.Max(1, outputBufferMs) / 1000.0)));
                }
            }
        }
    }

    public void UpdateConsumerTiming(string consumerId, int sampleRate, int outputBufferMs)
    {
        RegisterConsumer(consumerId, sampleRate, outputBufferMs);
    }

    public bool ShouldHoldForGlobalRefill()
    {
        lock (_syncLock)
        {
            if (!_globalRefillHoldActive) return false;
            if (_states.Count == 0) return true;

            foreach (var state in _states.Values)
            {
                int targetFrames = Math.Max(1, state.HoldTargetFrames);
                if (state.BufferedFrames < targetFrames)
                {
                    return true;
                }
            }

            _globalRefillHoldActive = false;
            return false;
        }
    }

    public void ArmGlobalRefillHold()
    {
        lock (_syncLock)
        {
            _globalRefillHoldActive = true;
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

    public void ReportPreparedSourceFrames(string consumerId, int sourceFrames)
    {
        lock (_syncLock)
        {
            if (!_states.TryGetValue(consumerId, out var state)) return;
            state.LastPreparedSourceFrames = Math.Max(0, sourceFrames);
        }
    }

    public int GetLastPreparedSourceFrames(string consumerId)
    {
        lock (_syncLock)
        {
            return _states.TryGetValue(consumerId, out var state)
                ? Math.Max(0, state.LastPreparedSourceFrames)
                : 0;
        }
    }

    public void ReportBufferedFrames(string consumerId, int bufferedFrames)
    {
        lock (_syncLock)
        {
            if (!_states.TryGetValue(consumerId, out var state)) return;

            state.BufferedFrames = Math.Max(0, bufferedFrames);

            // Update adaptive master target based on aggregate starvation
            RecomputeAdaptiveMasterTargetNoLock();

            if (!_states.ContainsKey(_masterConsumerId)) return;

            if (consumerId == _masterConsumerId)
            {
                // Master: immediate error is distance from adaptive target (drives master PI).
                state.SmoothedErrorFrames = state.BufferedFrames - _adaptiveMasterTargetFrames;
                return;
            }

            // Follower: error is the delta in input-ring consumption between this follower and the
            // master. Positive => follower has MORE buffered (consumed less) => follower is behind
            // master in source-content position => speed up (ratio > 1.0). This signal is in the
            // domain that the resampling ratio actually controls (source-frame consumption rate),
            // unlike a hardware-clock wall-time delta which the ratio cannot influence.
            if (!_states.TryGetValue(_masterConsumerId, out var masterState)) return;
            if (masterState.BufferedFrames < 0) return;

            // Immediate follower spread (no moving average): follower buffered minus master buffered.
            state.SmoothedErrorFrames = state.BufferedFrames - masterState.BufferedFrames;
        }
    }

    public void ReportConsumerRouteActivity(string consumerId, bool hasActiveRoutes)
    {
        lock (_syncLock)
        {
            if (!_states.TryGetValue(consumerId, out var state)) return;
            state.HasActiveRoutes = hasActiveRoutes;
        }
    }

    public bool IsConsumerRouteActive(string consumerId)
    {
        lock (_syncLock)
        {
            return _states.TryGetValue(consumerId, out var state) && state.HasActiveRoutes;
        }
    }

    public List<string> GetActiveConsumerIds()
    {
        lock (_syncLock)
        {
            var list = new List<string>(_states.Count);
            foreach (var pair in _states)
            {
                if (pair.Value.HasActiveRoutes)
                {
                    list.Add(pair.Key);
                }
            }

            return list;
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

    public double GetConsumerSmoothedErrorFrames(string consumerId)
    {
        lock (_syncLock)
        {
            return _states.TryGetValue(consumerId, out var state)
                ? state.SmoothedErrorFrames
                : 0;
        }
    }

    public double GetConsumerIntegralErrorFrames(string consumerId)
    {
        lock (_syncLock)
        {
            return _states.TryGetValue(consumerId, out var state)
                ? state.IntegralErrorFrames
                : 0;
        }
    }

    public double GetConsumerAppliedPpm(string consumerId)
    {
        lock (_syncLock)
        {
            return _states.TryGetValue(consumerId, out var state)
                ? state.LastAppliedPpm
                : 0;
        }
    }

    /// <summary>
    /// Per-block update of follower control state: FastCatchUp transitions and PI ratio.
    /// Master is held at ratio 1.0. Must be called once per follower Read() block AFTER
    /// the latest phase error has been reported.
    /// </summary>
    public void UpdateControlState(string consumerId)
    {
        lock (_syncLock)
        {
            if (!_states.TryGetValue(consumerId, out var state)) return;
            UpdateControlStateNoLock(consumerId, state);
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
                var masterFrames = masterState.BufferedFrames;
                // Return master's exact level; ignore learned bias to enforce tight sync
                return Math.Max(0, masterFrames);
            }

            return Math.Max(0, state.BufferedFrames);
        }
    }

    public double GetConsumerVariationRangeFrames(string consumerId)
    {
        lock (_syncLock)
        {
            if (!_states.TryGetValue(consumerId, out var state)) return 0;
            if (!_states.TryGetValue(_masterConsumerId, out var masterState)) return 0;

            // Follower variance: instantaneous spread to master.
            if (consumerId != _masterConsumerId)
            {
                return Math.Abs(state.BufferedFrames - masterState.BufferedFrames);
            }

            // Master variance: worst instantaneous follower spread to master.
            double masterBuffered = masterState.BufferedFrames;
            double worstSpread = 0;
            foreach (var pair in _states)
            {
                if (pair.Key == _masterConsumerId) continue;
                worstSpread = Math.Max(worstSpread, Math.Abs(pair.Value.BufferedFrames - masterBuffered));
            }

            return worstSpread;
        }
    }

    public double GetConsumerBufferedFrames(string consumerId)
    {
        lock (_syncLock)
        {
            if (!_states.TryGetValue(consumerId, out var state)) return 0;
            return Math.Max(0, state.BufferedFrames);
        }
    }

    public double GetConsumerSpreadToMasterFrames(string consumerId)
    {
        lock (_syncLock)
        {
            if (consumerId == _masterConsumerId) return 0;
            if (!_states.TryGetValue(consumerId, out var state)) return 0;
            if (!_states.TryGetValue(_masterConsumerId, out var masterState)) return 0;

            return state.BufferedFrames - masterState.BufferedFrames;
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

        // User-selected output buffer is the baseline floor. Any temporary underrun dip is
        // rebuilt quickly back to base once underruns stop.
        if (_effectiveMinTargetFrames < 0)
        {
            _effectiveMinTargetFrames = _baseMasterTargetFrames;
        }
        if (_recentUnderrunCount == 0)
        {
            _effectiveMinTargetFrames = _baseMasterTargetFrames;
        }

        desiredTargetFrames = Math.Clamp(desiredTargetFrames, (int)_effectiveMinTargetFrames, _maxMasterTargetFrames);
        
        _adaptiveMasterTargetFrames = desiredTargetFrames;
        
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
                _globalRefillHoldActive = true;

                // Permit a limited temporary dip in effective floor to recover from underruns,
                // but keep it close to the user-selected base.
                if (_effectiveMinTargetFrames < 0)
                {
                    _effectiveMinTargetFrames = _baseMasterTargetFrames;
                }
                int underrunMinTarget = Math.Max(1, (int)Math.Round(_baseMasterTargetFrames * MinTargetDuringUnderrunFraction));
                _effectiveMinTargetFrames = Math.Max(underrunMinTarget, _effectiveMinTargetFrames - Math.Max(1, underrunDelta));

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

    private void UpdateControlStateNoLock(string consumerId, OutputState state)
    {
        bool isMaster = consumerId == _masterConsumerId;
        if (isMaster)
        {
            state.FastCatchUpActive = false;
            state.FastCatchUpEnterConfirmBlocks = 0;
            state.FastCatchUpHoldBlocks = 0;
            state.PostRecoveryWindowBlocksRemaining = 0;
            state.Ratio = ComputeMasterRatioNoLock(state);
            return;
        }

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

        // PI ratio is the sole correction mechanism. With hardware-clock phase reporting
        // feeding SmoothedErrorFrames, the controller can drive sub-frame phase error to zero
        // continuously \u2014 no discrete frame slip needed.
        state.Ratio = ComputeFollowerRatioNoLock(state, state.FastCatchUpActive);
    }

    private double ComputeMasterRatioNoLock(OutputState state)
    {
        // Error is already populated by ReportBufferedFrames as (buffered - adaptiveTarget).
        // Sign convention: positive error => master has MORE buffered than target => master is
        // consuming too SLOWLY relative to producer => speed up (ratio > 1.0). Negative => slow down.
        double errorFrames = state.SmoothedErrorFrames;
        double currentRatio = state.Ratio <= 0 ? 1.0 : state.Ratio;

        // Startup gate: until buffered first crosses ~half the target, hold ratio at 1.0 and
        // don't integrate. Prevents huge initial wind-up while the ring is filling from empty.
        if (!state.PiArmed)
        {
            if (state.BufferedFrames >= _adaptiveMasterTargetFrames * 0.5)
            {
                state.PiArmed = true;
            }
            else
            {
                state.IntegralErrorFrames = 0;
                state.LastAppliedPpm = 0;
                return 1.0;
            }
        }

        // True integrator (no leak). Anti-windup is provided exclusively by the explicit clamp.
        // A leaky integrator would create steady-state error proportional to the constant clock
        // drift between input and output devices — the very thing we built this loop to cancel.
        // Always integrate; the deadband only suppresses the proportional kick.
        state.IntegralErrorFrames = Math.Clamp(
            state.IntegralErrorFrames + errorFrames,
            -RatioIntegralClampFrames,
            RatioIntegralClampFrames);

        double targetPpm = Math.Abs(errorFrames) <= MasterSettleBandFrames
            ? (state.IntegralErrorFrames * MasterRatioKiPpmPerIntegralFrame)
            : (errorFrames * MasterRatioKpPpmPerFrame) + (state.IntegralErrorFrames * MasterRatioKiPpmPerIntegralFrame);

        targetPpm = Math.Clamp(targetPpm, -MasterMaxRatioPpm, MasterMaxRatioPpm);

        double targetRatio = 1.0 + (targetPpm / 1_000_000.0);

        // Master gets the same step limit as a follower in normal mode — prevents audible flutter.
        double maxRatioStep = RatioStepLimitPpmPerBlock / 1_000_000.0;
        double boundedTargetRatio = Math.Clamp(targetRatio, currentRatio - maxRatioStep, currentRatio + maxRatioStep);

        double nextRatio = boundedTargetRatio;
        state.LastAppliedPpm = (nextRatio - 1.0) * 1_000_000.0;
        return nextRatio;
    }

    private double ComputeFollowerRatioNoLock(OutputState state, bool fastCatchUp)
    {
        double errorFrames = state.SmoothedErrorFrames;
        double currentRatio = state.Ratio;

        // Error definition: follower minus master buffered frames.
        // Positive error means follower has more queued audio (more delayed) and should speed up (>1.0).
        // Negative error means follower has less queued audio (less delayed) and should slow down (<1.0).
        double settleBand = fastCatchUp ? 6 : StableSettleBandFrames;
        double maxPpm = fastCatchUp ? FastCatchUpMaxFollowerRatioPpm : MaxFollowerRatioPpm;

        double kp = fastCatchUp ? FastCatchUpRatioKpPpmPerFrame : RatioKpPpmPerFrame;
        double ki = fastCatchUp ? FastCatchUpRatioKiPpmPerIntegralFrame : RatioKiPpmPerIntegralFrame;

        // Startup gate: don't engage PI until master has armed AND we have a valid follower buffered
        // measurement. Without this, follower integrates spurious errors during ring fill.
        if (!state.PiArmed)
        {
            if (_states.TryGetValue(_masterConsumerId, out var masterStateForArm)
                && masterStateForArm.PiArmed
                && state.BufferedFrames >= 0)
            {
                state.PiArmed = true;
            }
            else
            {
                state.IntegralErrorFrames = 0;
                state.LastAppliedPpm = 0;
                return 1.0;
            }
        }

        // True integrator. Always integrate — the deadband only gates the proportional kick.
        // Anti-windup via the explicit clamp; no leak (a leak would re-introduce steady-state error
        // proportional to constant inter-device clock drift).
        state.IntegralErrorFrames = Math.Clamp(
            state.IntegralErrorFrames + errorFrames,
            -RatioIntegralClampFrames,
            RatioIntegralClampFrames);

        double targetPpm = Math.Abs(errorFrames) <= settleBand
            ? (state.IntegralErrorFrames * ki)
            : (errorFrames * kp) + (state.IntegralErrorFrames * ki);

        targetPpm = Math.Clamp(targetPpm, -maxPpm, maxPpm);

        double targetRatio = 1.0 + (targetPpm / 1_000_000.0);

        if (currentRatio <= 0)
        {
            state.LastAppliedPpm = targetPpm;
            return targetRatio;
        }

        // Bound ratio slew-rate per processing block to avoid audible flutter from rapid direction changes.
        double maxRatioStep = (fastCatchUp ? FastCatchUpRatioStepLimitPpmPerBlock : RatioStepLimitPpmPerBlock) / 1_000_000.0;
        double boundedTargetRatio = Math.Clamp(targetRatio, currentRatio - maxRatioStep, currentRatio + maxRatioStep);

        double nextRatio = boundedTargetRatio;
        state.LastAppliedPpm = (nextRatio - 1.0) * 1_000_000.0;
        return nextRatio;
    }
}

/// <summary>
/// ISampleProvider that reads from capture ring buffers, applies routing matrix gains,
/// and mixes into the output for a specific render device.
/// </summary>
public class MixingSampleProvider : ISampleProvider
{
    private const int InputSyncSettleBandFrames = 64;
    private const int MetricsRmsWindowSamples = 96;

    private sealed class RollingRms
    {
        private readonly int _capacity;
        private readonly Queue<double> _samples;
        private double _sumSquares;
        private readonly object _lock = new();

        public RollingRms(int capacity)
        {
            _capacity = Math.Max(1, capacity);
            _samples = new Queue<double>(_capacity);
        }

        public void Push(double value)
        {
            double v = double.IsFinite(value) ? value : 0;
            lock (_lock)
            {
                _samples.Enqueue(v);
                _sumSquares += v * v;
                if (_samples.Count > _capacity)
                {
                    double old = _samples.Dequeue();
                    _sumSquares -= old * old;
                }
            }
        }

        public double GetRmsOrDefault(double fallback)
        {
            lock (_lock)
            {
                if (_samples.Count == 0)
                {
                    return fallback;
                }

                return Math.Sqrt(Math.Max(0, _sumSquares / _samples.Count));
            }
        }
    }

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
    private float[] _delayBuffer = [];
    private readonly WdlResampler _resampler;
    private int _delayWriteIndex;
    private int _deviceDelayMs;
    private int _outputBufferMs;
    private long _underrunCount;
    private readonly float[] _peakLevels;
    private readonly Dictionary<string, long> _inputSyncDiscardedFramesByDevice = new(StringComparer.Ordinal);
    private readonly RollingRms _variationFramesRms = new(MetricsRmsWindowSamples);

    public record struct CaptureSource(string DeviceId, RingBuffer Buffer, int GlobalChannelOffset, int Channels, bool IsMasterInput);

    public MixingSampleProvider(
        RoutingMatrix matrix,
        List<CaptureSource> sources,
        int outputChannelOffset,
        int outputChannels,
        int sampleRate,
        int outputDelayMs,
        int outputBufferMs,
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
        _deviceDelayMs = Math.Clamp(outputDelayMs, 0, 5000);
        _outputBufferMs = Math.Clamp(outputBufferMs, 10, 200);
        _syncCoordinator.RegisterConsumer(_consumerId, _sampleRate, _outputBufferMs);
        _syncCoordinator.ArmGlobalRefillHold();
        RebuildDelayBuffer();

        // Sinc resampler handles the small per-block rate adjustments (±3000 ppm) imposed by
        // the PI controller without the harmonics that linear interpolation introduces near
        // unity ratio. Output-driven mode (SetFeedMode=false) lets us request exactly `frames`
        // output samples per Read() and have the resampler tell us how many input samples it needs.
        _resampler = new WdlResampler();
        _resampler.SetMode(true, 0, true, 64, 32);
        _resampler.SetFilterParms();
        _resampler.SetFeedMode(false);
        _resampler.SetRates(_sampleRate, _sampleRate);
    }

    public WaveFormat WaveFormat => _waveFormat;
    public long UnderrunCount => Interlocked.Read(ref _underrunCount);
    public long DroppedFrames => GetDroppedFramesForConsumer();
    public double OutputVariationRangeMs
    {
        get
        {
            if (_sampleRate <= 0) return 0;
            return (_variationFramesRms.GetRmsOrDefault(ComputeCurrentVariationFrames()) * 1000.0) / _sampleRate;
        }
    }

    private double ComputeCurrentVariationFrames()
    {
        if (_sources.Count == 0) return 0;

        if (!_syncCoordinator.IsConsumerRouteActive(_consumerId))
        {
            return 0;
        }

        var activeConsumers = _syncCoordinator.GetActiveConsumerIds();
        if (activeConsumers.Count <= 1)
        {
            return 0;
        }

        var referenceSource = _sources.FirstOrDefault(IsMasterInputSource);
        var refBuffer = referenceSource.Buffer ?? _sources[0].Buffer;
        string masterId = _syncCoordinator.GetMasterConsumerId();

        bool masterIsActive = activeConsumers.Contains(masterId, StringComparer.Ordinal);

        if (_consumerId != masterId)
        {
            if (!masterIsActive)
            {
                double worstPeerSpread = 0;
                foreach (string peerId in activeConsumers)
                {
                    if (peerId == _consumerId) continue;
                    worstPeerSpread = Math.Max(
                        worstPeerSpread,
                        ResolvePhaseCompensatedSpreadFrames(
                            Math.Abs(refBuffer.GetReadPointerDiffFrames(peerId, _consumerId)),
                            peerId,
                            _consumerId));
                }

                return worstPeerSpread;
            }

            return ResolvePhaseCompensatedSpreadFrames(Math.Abs(refBuffer.GetReadPointerDiffFrames(masterId, _consumerId)), masterId, _consumerId);
        }

        double worstFrames = 0;
        foreach (string fid in activeConsumers)
        {
            if (fid == masterId) continue;
            worstFrames = Math.Max(worstFrames, ResolvePhaseCompensatedSpreadFrames(Math.Abs(refBuffer.GetReadPointerDiffFrames(masterId, fid)), masterId, fid));
        }
        return worstFrames;
    }

    private double ResolvePhaseCompensatedSpreadFrames(double rawSpreadFrames, string masterConsumerId, string followerConsumerId)
    {
        // Render callbacks are block-quantized and not phase-aligned across devices.
        // A one-block offset can appear as a jump in instantaneous pointer spread
        // even while audible sync remains locked. Compensate this deterministic ambiguity.
        int masterBlock = _syncCoordinator.GetLastPreparedSourceFrames(masterConsumerId);
        int followerBlock = _syncCoordinator.GetLastPreparedSourceFrames(followerConsumerId);
        int block = Math.Max(masterBlock, followerBlock);
        if (block <= 0)
        {
            return rawSpreadFrames;
        }

        double d = Math.Abs(rawSpreadFrames);
        double oneBlockFolded = Math.Abs(d - block);
        return Math.Min(d, oneBlockFolded);
    }
    public double OutputVariationOffsetMs => _sampleRate > 0
        ? (_syncCoordinator.GetConsumerSpreadToMasterFrames(_consumerId) * 1000.0) / _sampleRate
        : 0;
    public double OutputSyncErrorMs => _sampleRate > 0
        ? Math.Round((_syncCoordinator.GetConsumerSmoothedErrorFrames(_consumerId) * 1000.0) / _sampleRate, 2)
        : 0;
    public double OutputSyncIntegralMs => _sampleRate > 0
        ? Math.Round((_syncCoordinator.GetConsumerIntegralErrorFrames(_consumerId) * 1000.0) / _sampleRate, 2)
        : 0;
    public double OutputAppliedPpm => Math.Round(_syncCoordinator.GetConsumerAppliedPpm(_consumerId), 1);
    public bool FastCatchUpActive => _syncCoordinator.IsFastCatchUpActive(_consumerId);
    public double FastCatchUpDutyPercent => Math.Round(_syncCoordinator.GetFastCatchUpDutyPercent(_consumerId), 1);
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
        int clamped = Math.Clamp(bufferMs, 10, 200);
        if (_outputBufferMs != clamped)
        {
            _outputBufferMs = clamped;
            _syncCoordinator.UpdateConsumerTiming(_consumerId, _sampleRate, _outputBufferMs);
            _syncCoordinator.ArmGlobalRefillHold();
        }
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

        var front = _matrix.GetFrontBuffer();
        int matOutCh = _matrix.OutputChannels;
        float muteLinear = _matrix.TransientMuteAll ? 0f : 1f;

        bool HasAnyActiveRouteForThisOutput()
        {
            if (matOutCh <= 0 || front.Length == 0) return false;

            foreach (var source in _sources)
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
            }

            return false;
        }

        _syncCoordinator.ReportConsumerRouteActivity(_consumerId, HasAnyActiveRouteForThisOutput());

        int bufferedFrames = GetBufferedFramesForConsumer();
        _syncCoordinator.ReportBufferedFrames(_consumerId, bufferedFrames);
        _variationFramesRms.Push(ComputeCurrentVariationFrames());

        // Global refill barrier: on startup/underrun, all outputs wait until every output
        // reaches its output-buffer floor, then all release together.
        if (_syncCoordinator.ShouldHoldForGlobalRefill())
        {
            Array.Clear(buffer, offset, count);
            _syncCoordinator.OnFramesRendered(_consumerId, frames);
            return count;
        }

        _syncCoordinator.UpdateControlState(_consumerId);

        double ratio = _syncCoordinator.GetConsumerRatio(_consumerId);

        // Drive the sinc resampler. Setting input rate = sampleRate * ratio means: when the PI
        // controller wants the follower to play faster (ratio > 1) we consume more source frames
        // per output block, and the resampler smoothly converts that into exactly `frames` output
        // frames at the device's native rate.
        _resampler.SetRates(_sampleRate * ratio, _sampleRate);
        int sourceFrames = _resampler.ResamplePrepare(frames, _outputChannels, out var mixBuf, out var mixBase);
        _syncCoordinator.ReportPreparedSourceFrames(_consumerId, sourceFrames);
        if (sourceFrames <= 0)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }

        int sourceSamples = sourceFrames * _outputChannels;
        Array.Clear(mixBuf, mixBase, sourceSamples);

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

            // Only count partial reads as underruns when the deficit is large enough to be
            // audible (>= ~0.5 ms). With a tightly-locked PI loop the buffered level hovers
            // micro-frames above/below the per-block ask, producing 1-10 frame deficits on
            // half of all reads even though zero audio is actually lost in any meaningful sense
            // (the resampler zero-pads sub-millisecond gaps that are inaudible).
            int deficit = sourceFrames - framesRead;
            int audibleDeficitThreshold = Math.Max(8, _sampleRate / 2000); // ~0.5 ms, min 8 frames
            if (deficit >= audibleDeficitThreshold)
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
                        mixBuf[mixBase + f * _outputChannels + dstCh] += sample;
                    }
                }
            }

            // Consume the frames we read
            src.Buffer.ReadForConsumer(_consumerId, _sourceTempBuffer, 0, framesRead);
        }

        int inputStarvationFrames = Math.Max(0, sourceFrames - referenceBufferedFrames);
        _syncCoordinator.ReportInputStarvation(inputStarvationFrames);

        int produced = _resampler.ResampleOut(buffer, offset, sourceFrames, frames, _outputChannels);
        if (produced < frames)
        {
            // Resampler couldn't deliver a full block (insufficient input). Zero-fill the tail
            // to avoid emitting stale data from the WASAPI buffer.
            Array.Clear(buffer, offset + produced * _outputChannels, (frames - produced) * _outputChannels);
        }

        // Clamp post-resample to guard against numeric overshoot.
        for (int i = 0; i < count; i++)
        {
            buffer[offset + i] = ClampSample(buffer[offset + i]);
        }

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

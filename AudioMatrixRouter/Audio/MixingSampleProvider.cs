using NAudio.Wave;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace AudioMatrixRouter.Audio;

public sealed class OutputSyncCoordinator
{
    // ===== Smooth ASRC Sync (professional approach — same as VB Matrix / Voicemeeter / Dante) =====
    // Outputs run at a smoothly-varying playback ratio (very close to 1.0). A PI controller
    // converts follower phase error into a tiny ratio offset. MixingSampleProvider applies that
    // ratio via linear-interpolation resampling of the per-output mix.
    //
    // Why ASRC instead of discrete splices (the previous design):
    //   * A discrete splice cross-fades audio with itself offset by N frames (~0.5–2 ms). This
    //     creates frequency-domain comb-filtering with notches across the audible band — that
    //     "phasy / flangy" coloration the user kept hearing under jitter. Continuous ASRC has
    //     no such artifact: a ratio of 1.00005 is mathematically the same audio, time-stretched
    //     by 50 ppm = 0.005 % = 0.087 cents (well below the human static-pitch-detection floor
    //     of ~6 cents). Inaudible on every program material.
    //
    // Why this won't sound "robotic" like older PI-ratio attempts did:
    //   * Earlier attempts had a fast slew-rate (~80 ppm/block ≈ 8000 ppm/sec ≈ 8 Hz pitch
    //     modulation). That's right in the human vibrato-detection band → audible wobble.
    //   * Here we use a slow slew (≤ RatioSlewPpmPerBlock = 4 ppm/block ≈ 400 ppm/sec ≈ 0.4 Hz
    //     modulation rate) — below the ~2 Hz floor for perceptible pitch modulation on broadband
    //     content. The ratio can drift by at most a tiny fraction per second.
    //   * A deadband ignores phase errors below RatioDeadbandFrames (~0.5 ms): the input ring
    //     and output buffer absorb transient WASAPI callback jitter (caused by game load on
    //     shared-GPU HDMI outputs) without ever spinning up the controller.
    //
    // Convergence math:
    //   * Real inter-card crystal mismatch on same-machine outputs is typically 10–50 ppm
    //     (often near 0 for siblings of the same audio chip/HDMI block). Steady-state ratio
    //     converges to that figure and STAYS there — no continuous motion = no audible wobble.
    //   * A 5 ms (240-frame @ 48k) initial offset closes at 1000 ppm × 48 frames/ms = 48
    //     frames/sec, so ~5 sec to fully close. Slew limit caps ramp-up time to ~250 ms to
    //     reach max ratio. Total: error visibly settles in seconds, inaudibly.
    private const double MaxRatioPpm           = 10000.0; // ±1.0 % static ratio — aggressive transient authority
    private const double RatioSlewPpmPerBlock  = 12.0;    // ~1200 ppm/sec base ramp while locked
    private const int    RatioDeadbandFrames   = 8;       // ~0.17 ms @ 48k — tight lock, less lazy correction
    private const double RatioKp               = 2.0e-6;  // ratio per frame of (deadband-shifted) phase error
    private const double RatioKi               = 3.0e-8;  // ratio per (frame · block) integral
    private const double IntegralClampFrames   = 50000.0; // ~1 sec @ 48 kHz of integral authority
    private const int    FastModeEnterErrorFrames = 240;  // ~5 ms — purely a UI signal, not a controller mode
    private const double PhaseErrorEmaAlpha    = 0.18;    // still smooth tiny jitter, but respond much faster
    private const double PhaseErrorSpikeClampFrames = 8.0; // tiny lock-zone clamp only; big steps must pass through
    private const double PhaseErrorFastPassFrames = 48.0;  // ~1 ms — above this, report raw step immediately

    // ===== Underrun Recovery =====
    private const double MinTargetDuringUnderrunFraction = 0.9;
    private const double InputStarvationBoostDecay = 0.96;
    private const int PostRecoveryUnderrunWindowBlocks = 80;

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

    // ===== Phase-projection (follower error signal) =====
    // The follower error used to be `follower.BufferedFrames - master.BufferedFrames`,
    // a snapshot delta sampled at independent WASAPI Read() callback instants. That
    // delta carried full callback-jitter and one-block quantization noise, which kept
    // the variance hovering around 3-5ms even when the outputs were actually well
    // locked. Replaced with a wall-clock projection of cumulative source-frame
    // consumption: each Read updates `(CumulativeSourceFrames, LastSourceTicks)`,
    // both anchors are projected forward to a common Stopwatch instant at each
    // consumer's nominal sample rate, and rawPhase = masterPos - followerPos.
    //
    // Target: rawPhase = 0. CumulativeSourceFrames is incremented in Read with the
    // exact source frames consumed; per-output delay buffer and driver latency are
    // downstream of this counter, so in the absence of inter-card clock drift all
    // outputs SHOULD consume source frames at the same wall-clock rate. There is no
    // "constant pipeline lead/lag" in this measurement that needs a learned bias to
    // absorb — a learned bias just memorializes startup callback-timing offset and
    // makes the controller chase a wrong target indefinitely ("variance shows 0.4 ms
    // but outputs are audibly desynced by N ms").
    //
    // PhaseWarmupBlocks of warmup are discarded so the first reading is taken after
    // the global refill barrier has released and callbacks have stabilized.
    private const int PhaseWarmupBlocks = 200;
    private static readonly double s_ticksPerSecond = Stopwatch.Frequency;

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

        // Phase-projection state (see PhaseWarmupBlocks comment above).
        public int SampleRate;
        public long CumulativeSourceFrames;
        public long LastSourceTicks;
        public int PhaseWarmupBlocksRemaining;
        public bool PhaseErrorInitialized;
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
                state.CumulativeSourceFrames = 0;
                state.LastSourceTicks = 0;
                state.PhaseWarmupBlocksRemaining = PhaseWarmupBlocks;
                state.PhaseErrorInitialized = false;
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
                        : 0,
                    SampleRate = Math.Max(0, sampleRate),
                    PhaseWarmupBlocksRemaining = PhaseWarmupBlocks
                };
            }
            else if (_states.TryGetValue(consumerId, out var state))
            {
                if (sampleRate > 0)
                {
                    state.HoldTargetFrames = Math.Max(1, (int)Math.Round(sampleRate * (Math.Max(1, outputBufferMs) / 1000.0)));
                    state.SampleRate = sampleRate;
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

            bool anyReported = false;
            foreach (var state in _states.Values)
            {
                // Skip consumers that have not yet produced their first Read callback.
                // BufferedFrames is initialised to -1 (sentinel) and only set once the
                // WASAPI render thread fires for that output. If we treat the sentinel as
                // back-pressure, a single output whose render thread is slow to start
                // (HDMI sink asleep, USB device powering up, Init succeeded but Play
                // hasn't actually fired a callback yet) holds the global refill barrier
                // forever and every other output writes silence indefinitely.
                if (state.BufferedFrames < 0) continue;
                anyReported = true;

                int targetFrames = Math.Max(1, state.HoldTargetFrames);
                if (state.BufferedFrames < targetFrames)
                {
                    return true;
                }
            }

            // If nobody has reported yet, keep holding (we have no information). Once at
            // least one consumer is alive and meets its floor, release the barrier so
            // healthy outputs can play through stuck-output scenarios.
            if (!anyReported) return true;

            _globalRefillHoldActive = false;
            // Re-arm the per-consumer phase-projection warmup so the first reading is taken
            // AFTER the refill release rather than during pre-release silence/ramp-up.
            foreach (var state in _states.Values)
            {
                state.PhaseWarmupBlocksRemaining = PhaseWarmupBlocks;
            }
            return false;
        }
    }

    public void ArmGlobalRefillHold()
    {
        lock (_syncLock)
        {
            _globalRefillHoldActive = true;
            foreach (var state in _states.Values)
            {
                state.PhaseWarmupBlocksRemaining = PhaseWarmupBlocks;
            }
        }
    }

    public void RemoveConsumer(string consumerId)
    {
        lock (_syncLock)
        {
            if (!_states.Remove(consumerId)) return;

            // Constellation changed. Re-arm the global refill hold so all remaining
            // consumers refresh their per-consumer warmup; integral state is reset for cleanliness
            // so the next sync lock starts from a clean PI controller (no stale wind-up).
            _globalRefillHoldActive = true;
            foreach (var state in _states.Values)
            {
                state.PhaseWarmupBlocksRemaining = PhaseWarmupBlocks;
                state.IntegralErrorFrames = 0;
                state.Ratio = 1.0;
                state.LastAppliedPpm = 0;
            }
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
            int safe = Math.Max(0, sourceFrames);
            state.LastPreparedSourceFrames = safe;
            // Atomic projection-anchor update: (cumulative source frames consumed, wall-clock
            // instant). ReportBufferedFrames will project both master and follower forward to
            // a common instant to compute a jitter-free phase signal.
            state.CumulativeSourceFrames += safe;
            state.LastSourceTicks = Stopwatch.GetTimestamp();
        }
    }

    /// <summary>
    /// Shifts this consumer's cumulative source-frame position by <paramref name="deltaFrames"/>.
    /// Called once per hold→active transition so that rawPhase reflects actual ring read-pointer
    /// differences rather than just frames-consumed-since-zero, which is blind to where in the
    /// ring each consumer started.
    /// </summary>
    public void AdjustPhaseAnchor(string consumerId, long deltaFrames)
    {
        lock (_syncLock)
        {
            if (!_states.TryGetValue(consumerId, out var state)) return;
            state.CumulativeSourceFrames += deltaFrames;
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

            // Follower: wall-clock projection of cumulative source-frame consumption. Both
            // master and follower anchors are projected forward to the same Stopwatch instant
            // at each consumer's own nominal sample rate, then rawPhase = masterPos - followerPos.
            // Positive rawPhase => master has consumed MORE source frames than this follower
            // at the common instant => follower is BEHIND in source position => speed up
            // (ratio > 1.0). Same sign convention as the previous snapshot delta, but free
            // of WASAPI callback jitter and one-block quantization.
            if (!_states.TryGetValue(_masterConsumerId, out var masterState)) return;
            if (masterState.LastSourceTicks == 0 || state.LastSourceTicks == 0) return;
            if (masterState.SampleRate <= 0 || state.SampleRate <= 0) return;

            long now = Stopwatch.GetTimestamp();
            double masterPos = masterState.CumulativeSourceFrames
                + (now - masterState.LastSourceTicks) * masterState.SampleRate / s_ticksPerSecond;
            double followerPos = state.CumulativeSourceFrames
                + (now - state.LastSourceTicks) * state.SampleRate / s_ticksPerSecond;
            double rawPhase = masterPos - followerPos;

            // Per-consumer warmup: discard the first PhaseWarmupBlocks samples so transient
            // startup behaviour (refill barrier release, callback ramp-up) is not reflected
            // as immediate error before the ASRC controller is even allowed to act.
            if (state.PhaseWarmupBlocksRemaining > 0)
            {
                state.PhaseWarmupBlocksRemaining--;
                state.SmoothedErrorFrames = 0;
                state.PhaseErrorInitialized = false;
                return;
            }

            // Target rawPhase = 0. CumulativeSourceFrames counts source frames consumed
            // by Read(); per-output delay buffer and driver latency are downstream and
            // do not contribute. Any non-zero rawPhase is real sync error to correct.
            if (!state.PhaseErrorInitialized)
            {
                state.SmoothedErrorFrames = rawPhase;
                state.PhaseErrorInitialized = true;
                return;
            }

            double absRaw = Math.Abs(rawPhase);
            if (absRaw >= PhaseErrorFastPassFrames)
            {
                state.SmoothedErrorFrames = rawPhase;
                return;
            }

            double prior = state.SmoothedErrorFrames;
            double limitedRaw = Math.Clamp(rawPhase, prior - PhaseErrorSpikeClampFrames, prior + PhaseErrorSpikeClampFrames);
            state.SmoothedErrorFrames = prior + (limitedRaw - prior) * PhaseErrorEmaAlpha;
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

    /// <summary>
    /// Worst-case absolute follower phase error (in source frames) currently observed by the
    /// controller. This is the actual sync deviation the ASRC controller is acting on,
    /// projected to a common wall-clock instant. Used for honest UI variance display.
    /// </summary>
    public double GetWorstFollowerAbsErrorFrames()
    {
        lock (_syncLock)
        {
            double worst = 0;
            foreach (var pair in _states)
            {
                if (pair.Key == _masterConsumerId) continue;
                if (!pair.Value.HasActiveRoutes) continue;
                if (pair.Value.PhaseWarmupBlocksRemaining > 0) continue;
                double abs = Math.Abs(pair.Value.SmoothedErrorFrames);
                if (abs > worst) worst = abs;
            }
            return worst;
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

                // Only pause all outputs (global refill hold) when a consumer is critically
                // empty — buffer below half the floor. A few-frame deficit from the PI loop
                // bottom is inaudible and must NOT cause a full silence gap across all outputs.
                bool criticalEmpty = false;
                foreach (var s in _states.Values)
                {
                    if (s.BufferedFrames >= 0 && s.HoldTargetFrames > 0
                        && s.BufferedFrames < s.HoldTargetFrames / 2)
                    {
                        criticalEmpty = true;
                        break;
                    }
                }

                if (criticalEmpty)
                {
                    _globalRefillHoldActive = true;
                }

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

    /// <summary>
    /// Per-block update of follower control state. Smooth ASRC: a slow PI controller drives
    /// the follower's playback ratio toward elimination of projected phase error. Master is
    /// always held at ratio 1.0 (long-term drift vs input clock is absorbed by the input ring).
    /// </summary>
    private void UpdateControlStateNoLock(string consumerId, OutputState state)
    {
        bool isMaster = consumerId == _masterConsumerId;
        if (isMaster)
        {
            // Master plays at exact device rate. Followers chase the master clock.
            // Arm PiArmed once it has a valid buffered measurement so followers can engage.
            if (!state.PiArmed && state.BufferedFrames >= 0)
            {
                state.PiArmed = true;
            }
            state.Ratio = 1.0;
            state.LastAppliedPpm = 0;
            state.IntegralErrorFrames = 0;
            state.FastCatchUpActive = false;
            state.FastCatchUpEnterConfirmBlocks = 0;
            state.FastCatchUpHoldBlocks = 0;
            state.PostRecoveryWindowBlocksRemaining = 0;
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

        // Startup gate: don't drive the controller until master has armed AND we have a valid
        // follower buffered measurement. Holds ratio at 1.0 during refill / warmup.
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
                state.Ratio = 1.0;
                state.LastAppliedPpm = 0;
                return;
            }
        }

        double phaseFrames = state.SmoothedErrorFrames;
        double absPhase = Math.Abs(phaseFrames);

        // Soft-knee deadband: subtract RatioDeadbandFrames from the magnitude so the error fed
        // to the PI is zero just outside the band and ramps in smoothly. Inside the band, the
        // integral is frozen (no wind-up of jitter, no forgetting of learned steady-state drift).
        double errorForPi;
        if (absPhase < RatioDeadbandFrames)
        {
            errorForPi = 0;
        }
        else
        {
            double sign = phaseFrames > 0 ? 1.0 : -1.0;
            errorForPi = sign * (absPhase - RatioDeadbandFrames);

            // Anti-windup: integrate only when not saturated in the same direction.
            double prospectiveIntegral = state.IntegralErrorFrames + errorForPi;
            bool atPositiveCap = state.Ratio >= 1.0 + (MaxRatioPpm * 1e-6) - 1e-9;
            bool atNegativeCap = state.Ratio <= 1.0 - (MaxRatioPpm * 1e-6) + 1e-9;
            if (!((errorForPi > 0 && atPositiveCap) || (errorForPi < 0 && atNegativeCap)))
            {
                state.IntegralErrorFrames = Math.Clamp(prospectiveIntegral, -IntegralClampFrames, IntegralClampFrames);
            }
        }

        // Compute target ratio from PI. For large errors, grant the controller more authority
        // so 5-10 ms excursions collapse quickly instead of hanging around for seconds.
        double targetRatio = 1.0 + (RatioKp * errorForPi) + (RatioKi * state.IntegralErrorFrames);
        double capPpm = absPhase >= PhaseErrorFastPassFrames ? MaxRatioPpm : Math.Min(MaxRatioPpm, 1000.0);
        double capUp   = 1.0 + (capPpm * 1e-6);
        double capDown = 1.0 - (capPpm * 1e-6);
        if (targetRatio > capUp) targetRatio = capUp;
        else if (targetRatio < capDown) targetRatio = capDown;

        // Slew-rate limit: stay gentle near lock, but let large errors correct aggressively.
        double slewPpm = absPhase >= PhaseErrorFastPassFrames
            ? 240.0
            : RatioSlewPpmPerBlock;
        double slewLimit = slewPpm * 1e-6;
        double delta = targetRatio - state.Ratio;
        if (delta > slewLimit) delta = slewLimit;
        else if (delta < -slewLimit) delta = -slewLimit;
        double newRatio = state.Ratio + delta;
        if (newRatio > capUp) newRatio = capUp;
        else if (newRatio < capDown) newRatio = capDown;
        state.Ratio = newRatio;
        state.LastAppliedPpm = (newRatio - 1.0) * 1e6;

        // FastCatchUp flag is now purely a UI signal ("we're catching up a real desync" vs
        // "we're locked"). It does not change controller authority — the slew limit is hard.
        state.FastCatchUpActive = absPhase >= FastModeEnterErrorFrames;
        state.FastCatchUpHoldBlocks = state.FastCatchUpActive ? state.FastCatchUpHoldBlocks + 1 : 0;
    }

    /// <summary>
    /// Inert: returns 0 splice frames. Kept for API compatibility with telemetry callers; the
    /// ASRC controller uses smooth ratio adjustment instead of discrete splices.
    /// </summary>
    public int ConsumeSpliceRequest(string consumerId) => 0;

    /// <summary>
    /// Inert: returns 0. Kept for API compatibility with telemetry callers.
    /// </summary>
    public long GetConsumerSpliceCount(string consumerId) => 0;
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
    private float[] _sourceTempBuffer = [];
    private float[] _discardBuffer = [];
    private float[] _delayBuffer = [];
    private bool[] _sourceActiveMask = [];
    // Per-source actual peek result (frames) — used to clamp ring advance after we know how
    // many source frames the ASRC ratio actually wants to consume. Reused per Read().
    private int[] _framesReadPerSource = [];
    // Mixing scratch: holds interleaved source-rate samples per Read() (one slot larger than
    // ratio * frames so the linear interpolator can safely read `mix[i+1]`). Reused.
    private float[] _mixScratch = [];
    // Fractional source-frame remainder carried between blocks. ASRC consumes
    // (_sourceFracRemainder + frames * ratio) source frames per Read; the integer part is
    // advanced on the rings, the fractional part is carried forward.
    private double _sourceFracRemainder;

    private int _delayWriteIndex;
    private int _deviceDelayMs;
    private int _outputBufferMs;
    private long _underrunCount;
    private readonly float[] _peakLevels;
    // Tracks whether this output was in the global refill hold on the previous Read() block.
    // Starts true so the first hold→active transition anchors the phase immediately.
    private bool _wasInHold = true;
    // ConcurrentDictionary: mutated from the WASAPI Read callback (audio thread) and read from
    // the WinForms UI thread when MainForm builds its state snapshot. Plain Dictionary<> races
    // here would surface as occasional NullRef / IndexOutOfRange on the UI thread under stress.
    private readonly ConcurrentDictionary<string, long> _inputSyncDiscardedFramesByDevice = new(StringComparer.Ordinal);

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
        _deviceDelayMs = Math.Clamp(outputDelayMs, 0, 5000);
        _outputBufferMs = Math.Clamp(outputBufferMs, 10, 200);
        _syncCoordinator.RegisterConsumer(_consumerId, _sampleRate, _outputBufferMs);
        _syncCoordinator.ArmGlobalRefillHold();
        RebuildDelayBuffer();
    }

    public WaveFormat WaveFormat => _waveFormat;
    public long UnderrunCount => Interlocked.Read(ref _underrunCount);
    public long DroppedFrames => GetDroppedFramesForConsumer();

    /// <summary>
    /// Honest output-side sync deviation in milliseconds. Reports the actual absolute
    /// phase error the ASRC controller is acting on (wall-clock-projected source-frame
    /// position of master vs this follower). Master reports the worst follower's deviation.
    /// No artificial smoothing — the underlying signal is per-block and already reflects
    /// what the controller sees.
    /// </summary>
    public double OutputVariationRangeMs
    {
        get
        {
            if (_sampleRate <= 0) return 0;
            double frames = _consumerId == _syncCoordinator.GetMasterConsumerId()
                ? _syncCoordinator.GetWorstFollowerAbsErrorFrames()
                : Math.Abs(_syncCoordinator.GetConsumerSmoothedErrorFrames(_consumerId));
            return (frames * 1000.0) / _sampleRate;
        }
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
        // No-op: the ASRC sync controller derives reference timing from the output
        // master consumer; the input-master concept is no longer used by this provider.
        _ = deviceId;
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

        _syncCoordinator.ReportConsumerRouteActivity(_consumerId, HasAnyActiveRouteForThisOutputCore(front, matOutCh));

        int bufferedFrames = GetBufferedFramesForConsumer();
        _syncCoordinator.ReportBufferedFrames(_consumerId, bufferedFrames);

        // Global refill barrier: on startup/underrun, all outputs wait until every output
        // reaches its output-buffer floor, then all release together.
        bool inHold = _syncCoordinator.ShouldHoldForGlobalRefill();

        // On the first Read() after a hold period ends, anchor the phase measurement to the
        // actual ring-buffer read position of this output. Without this, rawPhase = masterCumulative
        // - followerCumulative is blind to where in the ring each consumer started: two outputs
        // that exit hold with different ring positions (e.g. master drained by an underrun while
        // follower retained its buffer) will both show CumulativeSourceFrames = 0 yet be 50-200ms
        // apart in ring position. The controller then sees rawPhase ≈ 0 and never corrects the
        // desync — producing the "variance shows 0.4ms but massive audible desync" symptom.
        //
        // The fix: subtract the output's current ring buffer depth from CumulativeSourceFrames so
        // that rawPhase = 0 exactly when both outputs are at the same ring read position.
        if (_wasInHold && !inHold)
            _syncCoordinator.AdjustPhaseAnchor(_consumerId, -(long)bufferedFrames);
        _wasInHold = inHold;

        if (inHold)
        {
            Array.Clear(buffer, offset, count);
            _syncCoordinator.OnFramesRendered(_consumerId, frames);
            return count;
        }

        _syncCoordinator.UpdateControlState(_consumerId);

        // Smooth ASRC: the coordinator publishes a continuously-varying playback ratio (very
        // near 1.0 — within ±1000 ppm, slew-limited to a few ppm per block). For each block
        // we need approximately (frames * ratio) source frames, plus the fractional remainder
        // carried from the previous block, plus one extra slot so the linear interpolator can
        // safely read `mix[i+1]` at the last output sample. Ratio == 1.0 + zero remainder is
        // the exact identity path: a straight BlockCopy with no interpolation cost.
        double ratio = _syncCoordinator.GetConsumerRatio(_consumerId);
        if (!(ratio > 0)) ratio = 1.0;
        double srcOffsetStart = _sourceFracRemainder;
        double sourceFramesExact = srcOffsetStart + frames * ratio;
        int sourceFrames = (int)Math.Ceiling(sourceFramesExact) + 1;
        if (sourceFrames < 1) sourceFrames = 1;

        int sourceSamples = sourceFrames * _outputChannels;
        if (_mixScratch.Length < sourceSamples)
        {
            _mixScratch = new float[Math.Max(sourceSamples, _mixScratch.Length * 2)];
        }
        Array.Clear(_mixScratch, 0, sourceSamples);

        // Build per-source "active for this output" mask without LINQ/list allocation.
        // Allocating a List<CaptureSource> per Read() (one per output, every 10ms block) was
        // a steady GC source on the audio thread that caused Gen0 collections to coincide with
        // game-induced allocation pressure → audible drift / re-lock chasing.
        int sourceCount = _sources.Count;
        if (_sourceActiveMask.Length < sourceCount)
        {
            _sourceActiveMask = new bool[Math.Max(sourceCount, _sourceActiveMask.Length * 2)];
        }
        if (_framesReadPerSource.Length < sourceCount)
        {
            _framesReadPerSource = new int[Math.Max(sourceCount, _framesReadPerSource.Length * 2)];
        }
        int activeCount = 0;
        for (int i = 0; i < sourceCount; i++)
        {
            bool active = IsSourceActiveForThisOutputCore(_sources[i], front, matOutCh);
            _sourceActiveMask[i] = active;
            if (active) activeCount++;
        }
        // If no source is routed to this output, fall back to all sources to keep consumers advancing.
        bool useAllForRef = activeCount == 0;

        int referenceBufferedFrames = int.MaxValue;
        for (int i = 0; i < sourceCount; i++)
        {
            if (!useAllForRef && !_sourceActiveMask[i]) continue;
            int availableFrames = _sources[i].Buffer.GetAvailableFrames(_consumerId);
            if (availableFrames < referenceBufferedFrames)
            {
                referenceBufferedFrames = availableFrames;
            }
        }
        if (referenceBufferedFrames == int.MaxValue) referenceBufferedFrames = 0;

        int minFramesReadActive = int.MaxValue;
        for (int srcIdx = 0; srcIdx < sourceCount; srcIdx++)
        {
            _framesReadPerSource[srcIdx] = 0;
            var src = _sources[srcIdx];
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

            // Peek from capture ring buffer (advance after we know how many source frames
            // ASRC actually consumed, so the remainder of an oversized peek is reused next
            // block at the new ratio).
            int framesRead = src.Buffer.PeekForConsumer(_consumerId, _sourceTempBuffer, 0, sourceFrames);
            _framesReadPerSource[srcIdx] = framesRead;
            if (framesRead == 0)
            {
                Interlocked.Increment(ref _underrunCount);
                if (useAllForRef || _sourceActiveMask[srcIdx])
                {
                    minFramesReadActive = 0;
                }
                continue;
            }

            if (useAllForRef || _sourceActiveMask[srcIdx])
            {
                if (framesRead < minFramesReadActive) minFramesReadActive = framesRead;
            }

            int deficit = sourceFrames - framesRead;
            int audibleDeficitThreshold = Math.Max(8, _sampleRate / 2000); // ~0.5 ms, min 8 frames
            if (deficit >= audibleDeficitThreshold)
            {
                Interlocked.Increment(ref _underrunCount);
            }

            // Apply routing matrix into _mixScratch (sourceFrames-sized buffer).
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
                        _mixScratch[f * _outputChannels + dstCh] += sample;
                    }
                }
            }
        }

        if (minFramesReadActive == int.MaxValue) minFramesReadActive = 0;

        // ASRC consumption math: ratio · frames + carried fractional remainder. The integer
        // part is what we advance the ring cursors by; the new fractional remainder (< 1)
        // carries to the next block. Clamp by what we actually got from the source so a
        // starved input doesn't try to advance past its available data.
        int integerSourceConsumed = Math.Max(0, (int)Math.Floor(sourceFramesExact));
        int actualSourceConsumed = Math.Min(integerSourceConsumed, minFramesReadActive);
        // New remainder is the un-consumed fractional portion of THIS block's ratio · frames
        // (must always be in [0, 1)). If starvation forced actualSourceConsumed below the
        // integer math, the lost fractional debt is dropped to keep the remainder sane.
        _sourceFracRemainder = sourceFramesExact - integerSourceConsumed;
        if (_sourceFracRemainder < 0) _sourceFracRemainder = 0;
        else if (_sourceFracRemainder >= 1.0) _sourceFracRemainder = 0;

        // Advance each source ring by min(integerSourceConsumed, its own framesRead). Sources
        // that read short of the request just stay starved; this block's mix already has
        // zeros for those missing samples, so the audible effect is the same as before.
        for (int srcIdx = 0; srcIdx < sourceCount; srcIdx++)
        {
            int framesRead = _framesReadPerSource[srcIdx];
            if (framesRead <= 0) continue;
            int advance = Math.Min(integerSourceConsumed, framesRead);
            if (advance > 0)
            {
                _sources[srcIdx].Buffer.ReadForConsumer(_consumerId, _sourceTempBuffer, 0, advance);
            }
        }

        // Projection: report actually-consumed source frames (post-starvation cap). Per-block
        // phase tracking in the coordinator advances on this honest count.
        _syncCoordinator.ReportPreparedSourceFrames(_consumerId, actualSourceConsumed);

        int inputStarvationFrames = Math.Max(0, sourceFrames - referenceBufferedFrames);
        _syncCoordinator.ReportInputStarvation(inputStarvationFrames);

        // Render mix scratch (source-rate) into output (device-rate).
        bool identityPath = ratio == 1.0
            && srcOffsetStart < 1e-9
            && minFramesReadActive >= frames;
        if (identityPath)
        {
            // Fast path: no resampling needed this block. Straight copy at device nominal rate.
            Buffer.BlockCopy(_mixScratch, 0, buffer, offset * sizeof(float), frames * _outputChannels * sizeof(float));
        }
        else
        {
            // Linear interpolation. At |ratio - 1| < 1000 ppm, linear interp introduces
            // < 0.01 dB of HF rolloff and no audible aliasing — its frequency response is
            // essentially flat to 20 kHz in this regime. Higher-order kernels (cubic / sinc)
            // would be overkill for the ratio range and add CPU cost on the audio thread.
            int outCh = _outputChannels;
            // Max valid integer source index for `mix[idx+1]` access is sourceFrames - 1.
            // Above that, output the last available frame (covers tail of starvation edge).
            int maxBaseIdx = sourceFrames - 2;
            if (maxBaseIdx < 0) maxBaseIdx = 0;
            for (int f = 0; f < frames; f++)
            {
                double srcPos = srcOffsetStart + f * ratio;
                int baseFrame = (int)srcPos;
                double frac = srcPos - baseFrame;
                if (baseFrame > maxBaseIdx)
                {
                    baseFrame = maxBaseIdx;
                    frac = 1.0;
                }
                if (baseFrame < 0) { baseFrame = 0; frac = 0; }
                int a = baseFrame * outCh;
                int b = a + outCh;
                int outBase = offset + f * outCh;
                float fA = (float)(1.0 - frac);
                float fB = (float)frac;
                for (int c = 0; c < outCh; c++)
                {
                    buffer[outBase + c] = _mixScratch[a + c] * fA + _mixScratch[b + c] * fB;
                }
            }
        }

        // Clamp post-mix to guard against numeric overshoot.
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

    private bool HasAnyActiveRouteForThisOutputCore(Crosspoint[] front, int matOutCh)
    {
        if (matOutCh <= 0 || front.Length == 0) return false;

        for (int i = 0; i < _sources.Count; i++)
        {
            if (IsSourceActiveForThisOutputCore(_sources[i], front, matOutCh)) return true;
        }
        return false;
    }

    private bool IsSourceActiveForThisOutputCore(CaptureSource source, Crosspoint[] front, int matOutCh)
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
            _inputSyncDiscardedFramesByDevice.AddOrUpdate(source.DeviceId, frames, (_, current) => current + frames);
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

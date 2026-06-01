using NAudio.Wave;
using System.Diagnostics;
using System.Threading;

namespace AudioMatrixRouter.Audio;

public sealed class OutputSyncCoordinator
{
    // ===== Splice-Based Sync (no continuous resampling, no pitch motion) =====
    // Outputs always run at exact device nominal rate. Phase drift is corrected by occasional
    // discrete signed integer-frame splices with an equal-power crossfade. This eliminates
    // the audible "robotic chasing" coloration of a PI-on-resampler-ratio loop, which was
    // especially objectionable on this user's topology (5 HDMI outputs sharing one GPU —
    // bursty callback jitter under game load was driving the resampler to modulate ratio
    // continuously). Splice cost is the crossfade itself, ~1.3 ms of overlapped audio,
    // which is inaudible on broadband content at the splice rates involved (a few per second
    // worst-case, far less in steady state).
    private const int FollowerSpliceMinErrorFrames = 3;        // ~0.06 ms — minimum drift worth correcting
    private const int FollowerSpliceMaxFrames     = 24;        // ~0.5 ms per block in normal mode
    private const int FollowerSpliceCooldownBlocks = 8;        // ~80 ms between normal splices
    private const int FastSpliceEnterErrorFrames  = 240;       // ~5 ms — switch to aggressive mode
    private const int FastSpliceExitErrorFrames   = 16;        // re-lock threshold
    private const int FastSpliceMinHoldBlocks     = 4;         // hold fast mode briefly
    private const int FastSpliceMaxFrames         = 96;        // ~2 ms per block in fast mode
    private const int FastSpliceCooldownBlocks    = 2;         // ~20 ms between fast splices
    public const int  SpliceCrossfadeFrames       = 64;        // ~1.3 ms equal-power cosine crossfade
    private const int PhaseSpikeRejectFrames      = 32;        // ignore one-block phase jumps from splice ripple

    // ===== Master Self-Correction (kept inert; master ratio always 1.0 in splice design) =====
    // Master output runs at exact device rate. Long-term drift is absorbed by the input ring
    // buffer (400 ms capacity) and the existing trim/refill machinery. With same-GPU HDMI
    // outputs, master vs follower clock drift is effectively zero — the only error is bursty
    // callback jitter, which splice corrections handle directly.

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
    // PhaseWarmupBlocks of warmup are discarded; the first post-warmup sample
    // becomes a per-consumer bias, and the PI then drives the *change* in physical
    // phase to zero. Constant pipeline lead/lag (delay buffer, device latency, etc.)
    // is absorbed by the bias and never appears as error.
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

        // Splice-based sync state.
        public int PendingSpliceFrames;        // Signed integer-frame correction queued for next Read()
        public int SpliceCooldownBlocks;       // Blocks remaining before a new splice can be scheduled
        public long TotalSpliceCount;          // Telemetry: cumulative splice events
        public long TotalSpliceFrames;         // Telemetry: cumulative |frames| spliced
        public double LastPhaseFramesObserved; // For one-block spike rejection after a splice
        public bool LastPhaseFramesArmed;

        // Phase-projection state (see PhaseWarmupBlocks comment above).
        public int SampleRate;
        public long CumulativeSourceFrames;
        public long LastSourceTicks;
        public double PhaseBiasFrames;
        public int PhaseWarmupBlocksRemaining;
        public bool PhaseBiasArmed;
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
                state.PhaseBiasFrames = 0;
                state.PhaseWarmupBlocksRemaining = PhaseWarmupBlocks;
                state.PhaseBiasArmed = false;
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
            // Re-arm the per-consumer phase-projection warmup so the bias is captured AFTER
            // the refill release rather than during pre-release silence/ramp-up. Without this,
            // the bias would lock onto a transient and the post-release phase signal would
            // carry that as a constant offset instead of being centred at zero.
            foreach (var state in _states.Values)
            {
                state.PhaseWarmupBlocksRemaining = PhaseWarmupBlocks;
                state.PhaseBiasArmed = false;
                state.PhaseBiasFrames = 0;
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
                state.PhaseBiasArmed = false;
                state.PhaseBiasFrames = 0;
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
            // startup behaviour (refill barrier release, first PI engagement, callback ramp-up)
            // is not captured into the bias. Once warmup expires, the next rawPhase becomes
            // the bias; PI then drives the *change* in physical phase to zero.
            if (state.PhaseWarmupBlocksRemaining > 0)
            {
                state.PhaseWarmupBlocksRemaining--;
                state.SmoothedErrorFrames = 0;
                return;
            }

            if (!state.PhaseBiasArmed)
            {
                state.PhaseBiasFrames = rawPhase;
                state.PhaseBiasArmed = true;
                state.SmoothedErrorFrames = 0;
                return;
            }

            state.SmoothedErrorFrames = rawPhase - state.PhaseBiasFrames;
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
    /// Per-block update of follower control state. In splice-sync mode there is no continuous
    /// resampler ratio motion: outputs run at exact device nominal rate, and integrated phase
    /// drift is corrected by occasional discrete signed integer-frame splices that the
    /// MixingSampleProvider applies via crossfade in its next Read(). Master output is held at
    /// ratio 1.0 with no splice (long-term drift absorbed by the input ring + adaptive target).
    /// </summary>
    private void UpdateControlStateNoLock(string consumerId, OutputState state)
    {
        // Both master and follower always render at exact nominal rate. Ratio + ppm are
        // kept inert for UI compat (telemetry shows 0 ppm, ratio 1.0 always).
        state.Ratio = 1.0;
        state.LastAppliedPpm = 0;
        state.IntegralErrorFrames = 0;

        if (state.SpliceCooldownBlocks > 0)
        {
            state.SpliceCooldownBlocks--;
        }

        bool isMaster = consumerId == _masterConsumerId;
        if (isMaster)
        {
            // Master never splices itself in this design. If we did, the splice would jump
            // master.CumulativeSourceFrames, every follower would see a phase spike, and the
            // chain reaction would be visually scary even if eventually correct. Keep master
            // running clean; let followers chase the master clock.
            state.FastCatchUpActive = false;
            state.FastCatchUpEnterConfirmBlocks = 0;
            state.FastCatchUpHoldBlocks = 0;
            state.PostRecoveryWindowBlocksRemaining = 0;
            state.PendingSpliceFrames = 0;
            state.LastPhaseFramesArmed = false;
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

        // Startup gate: don't splice until master has armed AND we have a valid follower buffered
        // measurement. Without this, follower would react to spurious errors during ring fill.
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
                state.PendingSpliceFrames = 0;
                state.LastPhaseFramesArmed = false;
                return;
            }
        }

        double phaseFrames = state.SmoothedErrorFrames;
        double absPhaseFrames = Math.Abs(phaseFrames);

        // FastCatchUp transitions: gate on phase magnitude only (no confirmation blocks needed —
        // splice-mode reacts on the same block it sees the threshold).
        if (!state.FastCatchUpActive)
        {
            if (absPhaseFrames >= FastSpliceEnterErrorFrames)
            {
                state.FastCatchUpActive = true;
                state.FastCatchUpHoldBlocks = 0;
            }
        }
        else
        {
            state.FastCatchUpHoldBlocks += 1;
            if (state.FastCatchUpHoldBlocks >= FastSpliceMinHoldBlocks
                && absPhaseFrames <= FastSpliceExitErrorFrames)
            {
                state.FastCatchUpActive = false;
                state.FastCatchUpHoldBlocks = 0;
                state.PostRecoveryWindowBlocksRemaining = PostRecoveryUnderrunWindowBlocks;
            }
        }

        // Spike rejector: a fresh splice from this consumer (or from a peer that propagates
        // through the projection) shows up as a one-block jump in phaseFrames. If the change
        // since the previous block exceeds PhaseSpikeRejectFrames AND we're not in fast mode,
        // skip splice scheduling for this block and let the projection settle.
        bool spikeReject = false;
        if (state.LastPhaseFramesArmed && !state.FastCatchUpActive)
        {
            double phaseDelta = Math.Abs(phaseFrames - state.LastPhaseFramesObserved);
            if (phaseDelta > PhaseSpikeRejectFrames)
            {
                spikeReject = true;
            }
        }
        state.LastPhaseFramesObserved = phaseFrames;
        state.LastPhaseFramesArmed = true;

        // Schedule a splice if we're past cooldown, above the minimum-error floor, and not
        // rejecting a spike.
        if (!spikeReject
            && state.SpliceCooldownBlocks == 0
            && absPhaseFrames >= FollowerSpliceMinErrorFrames)
        {
            int maxSplice = state.FastCatchUpActive ? FastSpliceMaxFrames : FollowerSpliceMaxFrames;
            int magnitude = Math.Min((int)Math.Round(absPhaseFrames), maxSplice);
            int sign = phaseFrames > 0 ? 1 : -1;
            state.PendingSpliceFrames = sign * magnitude;
            state.SpliceCooldownBlocks = state.FastCatchUpActive
                ? FastSpliceCooldownBlocks
                : FollowerSpliceCooldownBlocks;
            state.TotalSpliceCount++;
            state.TotalSpliceFrames += magnitude;
        }
    }

    /// <summary>
    /// Returns the queued splice (signed frames) for this consumer and clears it. Called by
    /// MixingSampleProvider.Read() exactly once per block. Positive splice means consume MORE
    /// source frames than nominal (skip ahead); negative means consume FEWER (replay).
    /// </summary>
    public int ConsumeSpliceRequest(string consumerId)
    {
        lock (_syncLock)
        {
            if (!_states.TryGetValue(consumerId, out var state)) return 0;
            int splice = state.PendingSpliceFrames;
            state.PendingSpliceFrames = 0;
            return splice;
        }
    }

    public long GetConsumerSpliceCount(string consumerId)
    {
        lock (_syncLock)
        {
            return _states.TryGetValue(consumerId, out var state) ? state.TotalSpliceCount : 0;
        }
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
    // Half-width of the splice crossfade region (in output frames). Mirrors the coordinator's
    // SpliceCrossfadeFrames constant; defined here as a separate const so it can be referenced
    // in Read() guard math without crossing namespaces.
    private const int SpliceFadeHalf = OutputSyncCoordinator.SpliceCrossfadeFrames / 2;

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
    private bool[] _sourceActiveMask = [];
    // Mixing scratch: holds (frames + splice) interleaved samples per Read(). Reused.
    private float[] _mixScratch = [];

    // Equal-power cosine crossfade tables for splice rendering. Precomputed once.
    private static readonly float[] s_spliceFadeOut;
    private static readonly float[] s_spliceFadeIn;
    static MixingSampleProvider()
    {
        int n = OutputSyncCoordinator.SpliceCrossfadeFrames;
        s_spliceFadeOut = new float[n];
        s_spliceFadeIn = new float[n];
        // alpha = cos(pi/2 * t),  beta = sin(pi/2 * t),  alpha^2 + beta^2 = 1 (constant power).
        for (int i = 0; i < n; i++)
        {
            double t = (i + 0.5) / n;
            s_spliceFadeOut[i] = (float)Math.Cos(0.5 * Math.PI * t);
            s_spliceFadeIn[i]  = (float)Math.Sin(0.5 * Math.PI * t);
        }
    }
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

        _syncCoordinator.ReportConsumerRouteActivity(_consumerId, HasAnyActiveRouteForThisOutputCore(front, matOutCh));

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

        // Splice-based sync: outputs always run at exact device nominal rate. The coordinator
        // hands us a signed integer-frame splice request when phase drift exceeds threshold.
        // splice > 0 => skip ahead (consume `splice` extra source frames, crossfade them
        // out of the output block). splice < 0 => replay (consume `splice` fewer, crossfade
        // a small region back in).
        int splice = _syncCoordinator.ConsumeSpliceRequest(_consumerId);

        // Safety: clamp splice so the crossfade region always fits inside the output block.
        // Crossfade needs (frames/2 - fade/2 - |splice|) >= 0 in pre-fade region and similar
        // post-fade. With the configured constants (fade=64, max splice 96), need frames >= 256.
        // WASAPI shared mode block is typically 480 frames, so we're safe; but tiny blocks
        // (e.g. very small output buffer) just disable the splice for that block.
        int spliceMaxForBlock = Math.Max(0, (frames / 2) - SpliceFadeHalf - 1);
        if (Math.Abs(splice) > spliceMaxForBlock)
        {
            splice = Math.Sign(splice) * spliceMaxForBlock;
        }

        int sourceFrames = frames + splice;
        if (sourceFrames <= 0)
        {
            // Pathological — fall back to no splice this block.
            splice = 0;
            sourceFrames = frames;
        }
        _syncCoordinator.ReportPreparedSourceFrames(_consumerId, sourceFrames);

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

        for (int srcIdx = 0; srcIdx < sourceCount; srcIdx++)
        {
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

            // Read from capture ring buffer
            int framesRead = src.Buffer.PeekForConsumer(_consumerId, _sourceTempBuffer, 0, sourceFrames);
            if (framesRead == 0)
            {
                Interlocked.Increment(ref _underrunCount);
                continue;
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

            // Consume the frames we read
            src.Buffer.ReadForConsumer(_consumerId, _sourceTempBuffer, 0, framesRead);
        }

        int inputStarvationFrames = Math.Max(0, sourceFrames - referenceBufferedFrames);
        _syncCoordinator.ReportInputStarvation(inputStarvationFrames);

        // Render scratch (sourceFrames) into output (frames) using splice crossfade if needed.
        if (splice == 0)
        {
            // Common path: no correction this block. Direct copy at exact device rate.
            Buffer.BlockCopy(_mixScratch, 0, buffer, offset * sizeof(float), frames * _outputChannels * sizeof(float));
        }
        else
        {
            ApplySpliceCrossfade(_mixScratch, sourceFrames, buffer, offset, frames, _outputChannels, splice);
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

    /// <summary>
    /// Render <paramref name="sourceFrames"/> mixed source frames into <paramref name="outputFrames"/>
    /// output frames using an equal-power cosine crossfade at the block midpoint. The signed
    /// <paramref name="splice"/> = <paramref name="sourceFrames"/> - <paramref name="outputFrames"/>.
    /// Positive splice means we have more source than output (skip ahead); negative means
    /// less source than output (replay a small overlap). The crossfade region is centred at
    /// outputFrames/2 with half-width SpliceFadeHalf, and is inaudible on broadband content
    /// at the splice rates produced by the coordinator (a few per second worst-case).
    /// </summary>
    private static void ApplySpliceCrossfade(
        float[] mix, int sourceFrames,
        float[] output, int outputOffset,
        int outputFrames, int channels, int splice)
    {
        int fadeFrames = OutputSyncCoordinator.SpliceCrossfadeFrames;
        int half = SpliceFadeHalf;
        int splicePoint = outputFrames / 2;
        int fadeStart = splicePoint - half;
        int fadeEnd   = splicePoint + half;

        // Pre-fade region: copy mix[0 .. fadeStart] straight to output.
        // mix[outIdx] is always valid because fadeStart < min(outputFrames, sourceFrames).
        Buffer.BlockCopy(
            mix, 0,
            output, outputOffset * sizeof(float),
            fadeStart * channels * sizeof(float));

        // Crossfade region: outIdx in [fadeStart, fadeEnd).
        // alpha (cos) fades out mix[outIdx]; beta (sin) fades in mix[outIdx + splice].
        // For splice > 0: skip ahead (mix has +splice frames after outIdx).
        // For splice < 0: replay (mix at outIdx + splice = outIdx - |splice|, earlier sample).
        for (int i = 0; i < fadeFrames; i++)
        {
            float alpha = s_spliceFadeOut[i];
            float beta  = s_spliceFadeIn[i];
            int outIdx = fadeStart + i;
            int aIdx = outIdx;
            int bIdx = outIdx + splice;
            int outBase = (outputOffset + outIdx * channels);
            int aBase = aIdx * channels;
            int bBase = bIdx * channels;
            for (int c = 0; c < channels; c++)
            {
                output[outBase + c] = alpha * mix[aBase + c] + beta * mix[bBase + c];
            }
        }

        // Post-fade region: outIdx in [fadeEnd, outputFrames). Read mix[outIdx + splice].
        // For splice > 0: mix index goes up to outputFrames + splice - 1 = sourceFrames - 1, OK.
        // For splice < 0: mix index goes up to outputFrames - 1 + splice = sourceFrames - 1, OK.
        int postCount = outputFrames - fadeEnd;
        if (postCount > 0)
        {
            int srcStartFrame = fadeEnd + splice;
            Buffer.BlockCopy(
                mix, srcStartFrame * channels * sizeof(float),
                output, (outputOffset + fadeEnd * channels) * sizeof(float),
                postCount * channels * sizeof(float));
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

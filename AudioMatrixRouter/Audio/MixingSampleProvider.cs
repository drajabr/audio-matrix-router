using NAudio.Wave;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace AudioMatrixRouter.Audio;

/// <summary>
/// Output synchronisation coordinator — full rewrite.
///
/// ARCHITECTURE (single clock domain, one loop per device, measured phase):
///
///   * The ENGINE CLOCK is the master output's render callback. All ring buffers contain
///     audio already converted to the engine sample rate by the capture-side InputAsrc,
///     which also absorbs capture-crystal drift (so the master/input relationship can no
///     longer walk off — the failure mode of the previous design, whose master ran open-
///     loop at ratio 1.0 against an undisciplined input clock).
///
///   * Each consumer (output device) maintains an exact double-precision TIMELINE
///     POSITION: cumulative engine-rate source frames its playback has advanced by since
///     the last start barrier. This is reported from real per-block consumption
///     (frames * effectiveRatio) — an exact bookkeeping quantity, not a wall-clock
///     estimate. Follower phase error = master position (projected at the engine rate
///     over the sub-block report skew) minus follower position. No warmups, no learned
///     anchors, no adaptive targets.
///
///   * One PI loop per follower drives a ppm-scale TRIM on top of the follower's static
///     rate-conversion ratio (engineRate / deviceRate). Master trim is always 0.
///     Deadband + slew keep corrections far below audibility; anti-windup keeps the
///     integrator honest at the caps.
///
///   * The only barrier is the START barrier: outputs emit silence until every consumer's
///     ring fill reaches its target, then all timelines zero together and playback starts
///     phase-aligned. Underruns afterwards do NOT re-arm a global hold (the old global
///     refill hold silenced every healthy output and re-anchored phase, amplifying a
///     single device's hiccup into a system-wide desync event).
/// </summary>
public sealed class OutputSyncCoordinator
{
    // ===== Follower PI tuning =====
    // Plant: phase error integrates (ppm trim → frames/s closure), so PI gives zero
    // steady-state error against constant residual drift.
    private const double MaxTrimPpm = 2000.0;       // far above real same-machine output drift
    private const double SlewPpmPerBlock = 4.0;     // ~400 ppm/s at 100 blocks/s — inaudible
    private const double FastSlewPpmPerBlock = 80.0;// engaged only for big (>5 ms) errors
    private const int DeadbandFrames = 12;          // ~0.25 ms @ 48k
    private const double Kp = 0.5;                  // ppm per frame of deadband-shifted error
    private const double Ki = 0.004;                // ppm per frame·block of integral
    private const double IntegralClampFrames = 24000.0;
    private const double ErrorEmaAlpha = 0.3;
    private const double ErrorFastPassFrames = 48.0; // ~1 ms: big steps pass unsmoothed
    private const double FastModeErrorFrames = 240.0; // ~5 ms: telemetry "catching up" flag

    private readonly object _lock = new();
    private readonly Dictionary<string, OutputState> _states = new(StringComparer.Ordinal);
    private string _masterConsumerId;
    private int _engineSampleRate;
    private int _baseMasterTargetFrames;
    private int _maxMasterTargetFrames;
    private bool _startBarrierActive = true;
    private long _totalUnderruns;
    private static readonly double s_ticksPerSecond = Stopwatch.Frequency;

    private sealed class OutputState
    {
        public int DeviceSampleRate;
        public int HoldTargetFrames;        // engine frames of ring fill required to release barrier
        public int BufferedFrames = -1;     // last reported ring fill (engine frames); -1 = no report yet
        public bool HasActiveRoutes;
        public long FramesRendered;         // device frames pushed to WASAPI
        public double TimelinePos;          // engine-rate source frames consumed since barrier release
        public long LastReportTicks;        // Stopwatch instant of last timeline report
        public double SmoothedErrorFrames;  // engine frames; + → behind master → speed up
        public bool ErrorInitialized;
        public double TrimPpm;
        public double IntegralFrames;
        public double LastAppliedPpm;
        public bool FastCatchUpActive;
        public long FastCatchUpFrames;
        public long LastObservedFrames;
        public long UnderrunsSinceStart;
    }

    public OutputSyncCoordinator(string masterConsumerId, int engineSampleRate, int baseMasterTargetFrames, int maxMasterTargetFrames)
    {
        _masterConsumerId = masterConsumerId ?? string.Empty;
        _engineSampleRate = Math.Max(1, engineSampleRate);
        _baseMasterTargetFrames = Math.Max(1, baseMasterTargetFrames);
        _maxMasterTargetFrames = Math.Max(_baseMasterTargetFrames, maxMasterTargetFrames);
    }

    public string GetMasterConsumerId() => _masterConsumerId;

    public void SetMasterConsumer(string masterConsumerId)
    {
        lock (_lock)
        {
            _masterConsumerId = masterConsumerId ?? string.Empty;
            ResetAllNoLock();
        }
    }

    public void SetMasterBufferTarget(int baseMasterTargetFrames, int maxMasterTargetFrames)
    {
        lock (_lock)
        {
            _baseMasterTargetFrames = Math.Max(1, baseMasterTargetFrames);
            _maxMasterTargetFrames = Math.Max(_baseMasterTargetFrames, maxMasterTargetFrames);
        }
    }

    public void RegisterConsumer(string consumerId, int deviceSampleRate, int outputBufferMs)
    {
        lock (_lock)
        {
            if (!_states.TryGetValue(consumerId, out var s))
            {
                s = new OutputState();
                _states[consumerId] = s;
            }
            if (deviceSampleRate > 0) s.DeviceSampleRate = deviceSampleRate;
            s.HoldTargetFrames = Math.Max(1,
                (int)Math.Round(_engineSampleRate * (Math.Max(1, outputBufferMs) / 1000.0)));
        }
    }

    public void UpdateConsumerTiming(string consumerId, int deviceSampleRate, int outputBufferMs)
        => RegisterConsumer(consumerId, deviceSampleRate, outputBufferMs);

    public void RemoveConsumer(string consumerId)
    {
        lock (_lock)
        {
            if (!_states.Remove(consumerId)) return;
            // Constellation changed: clean controller state so remaining followers re-lock
            // from a fresh integrator (no stale wind-up), but do NOT silence anyone.
            foreach (var s in _states.Values)
            {
                s.IntegralFrames = 0;
            }
        }
    }

    /// <summary>Re-arms the start barrier (engine start / explicit restart).</summary>
    public void ArmGlobalRefillHold()
    {
        lock (_lock)
        {
            _startBarrierActive = true;
            ResetAllNoLock();
        }
    }

    private void ResetAllNoLock()
    {
        foreach (var s in _states.Values)
        {
            s.BufferedFrames = -1;
            s.TimelinePos = 0;
            s.LastReportTicks = 0;
            s.SmoothedErrorFrames = 0;
            s.ErrorInitialized = false;
            s.TrimPpm = 0;
            s.IntegralFrames = 0;
            s.LastAppliedPpm = 0;
            s.FastCatchUpActive = false;
            s.FastCatchUpFrames = 0;
            s.LastObservedFrames = s.FramesRendered;
            s.UnderrunsSinceStart = 0;
        }
    }

    /// <summary>
    /// True while outputs should emit silence waiting for the start prefill. Releases when
    /// every consumer that has reported a fill measurement meets its target; at release all
    /// timelines are zeroed together so phase zero == identical ring cursors.
    /// </summary>
    public bool ShouldHoldForGlobalRefill()
    {
        lock (_lock)
        {
            if (!_startBarrierActive) return false;
            if (_states.Count == 0) return true;

            bool anyReported = false;
            foreach (var s in _states.Values)
            {
                if (s.BufferedFrames < 0) continue; // render thread not alive yet — don't block on it
                anyReported = true;
                if (s.BufferedFrames < Math.Max(1, s.HoldTargetFrames)) return true;
            }
            if (!anyReported) return true;

            _startBarrierActive = false;
            long now = Stopwatch.GetTimestamp();
            foreach (var s in _states.Values)
            {
                s.TimelinePos = 0;
                s.LastReportTicks = now;
                s.SmoothedErrorFrames = 0;
                s.ErrorInitialized = false;
                s.IntegralFrames = 0;
                s.TrimPpm = 0;
            }
            return false;
        }
    }

    public void ReportBufferedFrames(string consumerId, int bufferedFrames)
    {
        lock (_lock)
        {
            if (_states.TryGetValue(consumerId, out var s))
            {
                s.BufferedFrames = Math.Max(0, bufferedFrames);
            }
        }
    }

    public void ReportConsumerRouteActivity(string consumerId, bool hasActiveRoutes)
    {
        lock (_lock)
        {
            if (_states.TryGetValue(consumerId, out var s))
            {
                s.HasActiveRoutes = hasActiveRoutes;
            }
        }
    }

    /// <summary>
    /// Reports exact engine-rate source-frame consumption for this block and (for
    /// followers) updates the phase error and PI trim. Returns the trim (ppm) the
    /// caller should apply on its NEXT resampling pass.
    ///
    /// The timeline advances by frames * effectiveRatio every rendered block — including
    /// starved blocks (the device still played that wall-clock time; missing audio was
    /// zeros). Ring-cursor realignment in the provider keeps cursors equal to the
    /// timeline, so this number is the truth of playback position.
    /// </summary>
    public double ReportConsumedAndUpdate(string consumerId, double engineFramesConsumed, int deviceFramesRendered)
    {
        lock (_lock)
        {
            if (!_states.TryGetValue(consumerId, out var s)) return 0;

            long now = Stopwatch.GetTimestamp();
            s.TimelinePos += Math.Max(0, engineFramesConsumed);
            s.LastReportTicks = now;
            s.FramesRendered += Math.Max(0, deviceFramesRendered);

            bool isMaster = consumerId == _masterConsumerId;
            if (isMaster)
            {
                s.TrimPpm = 0;
                s.LastAppliedPpm = 0;
                s.IntegralFrames = 0;
                s.FastCatchUpActive = false;
                return 0;
            }

            if (!_states.TryGetValue(_masterConsumerId, out var m) || m.LastReportTicks == 0)
            {
                // Master not alive (failed device, mid-restart): hold trim steady.
                return s.TrimPpm;
            }

            // Stale-master guard: if the master's render thread has stopped reporting
            // (device unplugged / driver stall), the wall-clock projection would grow
            // without bound and drive every follower to its trim cap, desynchronising the
            // survivors against each other. Freeze the loop instead and let device
            // recovery (RefreshDevices → restart) re-anchor cleanly.
            double masterAgeSec = (now - m.LastReportTicks) / s_ticksPerSecond;
            if (masterAgeSec > 0.5)
            {
                return s.TrimPpm;
            }

            // Project the master's timeline to 'now' at the ENGINE rate (both timelines are
            // in engine frames, so this is dimensionally exact regardless of device rates).
            double masterPos = m.TimelinePos + (now - m.LastReportTicks) * _engineSampleRate / s_ticksPerSecond;
            double rawError = masterPos - s.TimelinePos; // + → we are behind → speed up

            // Light smoothing for callback-instant jitter; big steps pass through raw so a
            // genuine desync is acted on immediately rather than averaged away.
            if (!s.ErrorInitialized)
            {
                s.SmoothedErrorFrames = rawError;
                s.ErrorInitialized = true;
            }
            else if (Math.Abs(rawError) >= ErrorFastPassFrames)
            {
                s.SmoothedErrorFrames = rawError;
            }
            else
            {
                s.SmoothedErrorFrames += (rawError - s.SmoothedErrorFrames) * ErrorEmaAlpha;
            }

            double err = s.SmoothedErrorFrames;
            double absErr = Math.Abs(err);

            // Soft-knee deadband.
            double effErr = absErr <= DeadbandFrames
                ? 0
                : (err > 0 ? err - DeadbandFrames : err + DeadbandFrames);

            // Anti-windup: don't integrate further into a saturated cap.
            bool satHi = s.TrimPpm >= MaxTrimPpm - 1e-9;
            bool satLo = s.TrimPpm <= -MaxTrimPpm + 1e-9;
            if (!((effErr > 0 && satHi) || (effErr < 0 && satLo)))
            {
                s.IntegralFrames = Math.Clamp(s.IntegralFrames + effErr, -IntegralClampFrames, IntegralClampFrames);
            }

            double targetTrim = Math.Clamp(Kp * effErr + Ki * s.IntegralFrames, -MaxTrimPpm, MaxTrimPpm);
            double slew = absErr >= FastModeErrorFrames ? FastSlewPpmPerBlock : SlewPpmPerBlock;
            s.TrimPpm += Math.Clamp(targetTrim - s.TrimPpm, -slew, slew);
            s.LastAppliedPpm = s.TrimPpm;

            // Telemetry: are we visibly walking back a real desync?
            bool fast = absErr >= FastModeErrorFrames;
            if (fast && deviceFramesRendered > 0)
            {
                s.FastCatchUpFrames += deviceFramesRendered;
            }
            s.FastCatchUpActive = fast;

            return s.TrimPpm;
        }
    }

    public void ReportUnderruns(string consumerId, long underrunDelta)
    {
        if (underrunDelta <= 0) return;
        lock (_lock)
        {
            _totalUnderruns += underrunDelta;
            if (_states.TryGetValue(consumerId, out var s))
            {
                s.UnderrunsSinceStart += underrunDelta;
                // Underruns deliberately do NOT re-arm the global barrier and do NOT touch
                // timelines/integrators here. The provider freezes consumption truthfully
                // (cursor realign) and the PI walks the phase back smoothly.
            }
        }
    }

    public long GetTotalUnderruns()
    {
        lock (_lock) { return _totalUnderruns; }
    }

    // ===== Telemetry accessors (UI thread) =====

    public double GetConsumerSmoothedErrorFrames(string consumerId)
    {
        lock (_lock)
        {
            return _states.TryGetValue(consumerId, out var s) ? s.SmoothedErrorFrames : 0;
        }
    }

    public double GetWorstFollowerAbsErrorFrames()
    {
        lock (_lock)
        {
            double worst = 0;
            foreach (var pair in _states)
            {
                if (pair.Key == _masterConsumerId) continue;
                if (!pair.Value.HasActiveRoutes) continue;
                double abs = Math.Abs(pair.Value.SmoothedErrorFrames);
                if (abs > worst) worst = abs;
            }
            return worst;
        }
    }

    public double GetConsumerIntegralErrorFrames(string consumerId)
    {
        lock (_lock)
        {
            return _states.TryGetValue(consumerId, out var s) ? s.IntegralFrames : 0;
        }
    }

    public double GetConsumerAppliedPpm(string consumerId)
    {
        lock (_lock)
        {
            return _states.TryGetValue(consumerId, out var s) ? s.LastAppliedPpm : 0;
        }
    }

    public double GetConsumerSpreadToMasterFrames(string consumerId)
    {
        lock (_lock)
        {
            if (consumerId == _masterConsumerId) return 0;
            if (!_states.TryGetValue(consumerId, out var s)) return 0;
            if (!_states.TryGetValue(_masterConsumerId, out var m)) return 0;
            if (s.BufferedFrames < 0 || m.BufferedFrames < 0) return 0;
            return s.BufferedFrames - m.BufferedFrames;
        }
    }

    public bool IsFastCatchUpActive(string consumerId)
    {
        lock (_lock)
        {
            return _states.TryGetValue(consumerId, out var s) && s.FastCatchUpActive;
        }
    }

    public double GetFastCatchUpDutyPercent(string consumerId)
    {
        lock (_lock)
        {
            if (!_states.TryGetValue(consumerId, out var s) || s.FramesRendered <= 0) return 0;
            return Math.Clamp(s.FastCatchUpFrames * 100.0 / s.FramesRendered, 0, 100);
        }
    }

    public long GetPostRecoveryUnderruns(string consumerId)
    {
        lock (_lock)
        {
            return _states.TryGetValue(consumerId, out var s) ? s.UnderrunsSinceStart : 0;
        }
    }

    public int GetEngineSampleRate() => _engineSampleRate;
}

/// <summary>
/// ISampleProvider for one render device. Reads engine-rate audio from the shared
/// capture rings, applies the routing matrix, and resamples engine-rate → device-rate
/// with a ppm-scale sync trim from the coordinator.
///
/// Key invariant: this consumer's ring cursors always equal the integer part of its
/// engine-frame timeline. If a starved source delivers late, the late frames are
/// SKIPPED on arrival (counted in input-sync corrections) instead of being played
/// behind every other output — so all outputs always render the same instant of the
/// source material, and phase derived from timelines is the truth of the cursors.
/// </summary>
public class MixingSampleProvider : ISampleProvider
{
    private readonly RoutingMatrix _matrix;
    private readonly List<CaptureSource> _sources;
    private readonly int _outputChannelOffset;
    private readonly int _outputChannels;
    private readonly int _sampleRate;        // device rate
    private readonly int _engineSampleRate;  // ring/content rate
    private readonly double _baseRatio;      // engine frames consumed per device frame
    private readonly string _consumerId;
    private readonly OutputSyncCoordinator _syncCoordinator;
    private readonly object _delayLock = new();
    private readonly WaveFormat _waveFormat;

    private float[] _sourceTempBuffer = [];
    private float[] _mixScratch = [];
    private float[] _delayBuffer = [];
    private bool[] _sourceActiveMask = [];
    private int[] _framesReadPerSource = [];
    private long[] _pendingSkipPerSource = [];

    private double _srcFrac;        // fractional engine-frame offset carried between blocks
    private double _currentTrimPpm; // trim applied this block (updated post-block from coordinator)
    private int _delayWriteIndex;
    private int _deviceDelayMs;
    private int _outputBufferMs;
    private long _underrunCount;
    private readonly float[] _peakLevels;
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
        _sampleRate = Math.Max(1, sampleRate);
        _consumerId = consumerId;
        _syncCoordinator = syncCoordinator;
        _engineSampleRate = Math.Max(1, syncCoordinator.GetEngineSampleRate());
        _baseRatio = (double)_engineSampleRate / _sampleRate;
        _waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, outputChannels);
        _peakLevels = new float[outputChannels];
        _deviceDelayMs = Math.Clamp(outputDelayMs, 0, 5000);
        _outputBufferMs = Math.Clamp(outputBufferMs, 10, 200);
        _pendingSkipPerSource = new long[Math.Max(1, sources.Count)];
        _syncCoordinator.RegisterConsumer(_consumerId, _sampleRate, _outputBufferMs);
        // Pin this consumer's cursor on every ring NOW (before captures produce data) so
        // all consumers share an identical zero reference.
        foreach (var src in _sources)
        {
            src.Buffer.RegisterConsumer(_consumerId);
        }
        RebuildDelayBuffer();
    }

    public WaveFormat WaveFormat => _waveFormat;
    public long UnderrunCount => Interlocked.Read(ref _underrunCount);
    public long DroppedFrames => GetDroppedFramesForConsumer();

    /// <summary>Actual absolute phase error the controller is acting on, in ms.
    /// Master reports the worst follower's deviation.</summary>
    public double OutputVariationRangeMs
    {
        get
        {
            double frames = _consumerId == _syncCoordinator.GetMasterConsumerId()
                ? _syncCoordinator.GetWorstFollowerAbsErrorFrames()
                : Math.Abs(_syncCoordinator.GetConsumerSmoothedErrorFrames(_consumerId));
            return frames * 1000.0 / _engineSampleRate;
        }
    }

    public double OutputVariationOffsetMs =>
        _syncCoordinator.GetConsumerSpreadToMasterFrames(_consumerId) * 1000.0 / _engineSampleRate;

    public double OutputSyncErrorMs =>
        Math.Round(_syncCoordinator.GetConsumerSmoothedErrorFrames(_consumerId) * 1000.0 / _engineSampleRate, 2);

    public double OutputSyncIntegralMs =>
        Math.Round(_syncCoordinator.GetConsumerIntegralErrorFrames(_consumerId) * 1000.0 / _engineSampleRate, 2);

    public double OutputAppliedPpm => Math.Round(_syncCoordinator.GetConsumerAppliedPpm(_consumerId), 1);
    public bool FastCatchUpActive => _syncCoordinator.IsFastCatchUpActive(_consumerId);
    public double FastCatchUpDutyPercent => Math.Round(_syncCoordinator.GetFastCatchUpDutyPercent(_consumerId), 1);
    public long PostRecoveryUnderruns => _syncCoordinator.GetPostRecoveryUnderruns(_consumerId);

    public float[] PeekPeakLevels()
    {
        var snapshot = new float[_peakLevels.Length];
        for (int i = 0; i < _peakLevels.Length; i++) snapshot[i] = _peakLevels[i];
        return snapshot;
    }

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
        }
    }

    /// <summary>Kept for API compatibility; reference timing is the output master.</summary>
    public void SetInputMasterDevice(string deviceId)
    {
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
        _syncCoordinator.ReportBufferedFrames(_consumerId, GetBufferedFramesForConsumer());

        // Start barrier only: prefill all outputs, release together, timelines zero together.
        if (_syncCoordinator.ShouldHoldForGlobalRefill())
        {
            Array.Clear(buffer, offset, count);
            return count;
        }

        int sourceCount = _sources.Count;
        EnsureScratchSizes(sourceCount);

        // ===== 1. Effective consumption ratio for this block =====
        double effRatio = _baseRatio * (1.0 + _currentTrimPpm * 1e-6);
        if (!(effRatio > 0)) effRatio = _baseRatio;
        double srcOffsetStart = _srcFrac;
        double sourceFramesExact = srcOffsetStart + frames * effRatio;
        int integerConsume = (int)Math.Floor(sourceFramesExact);
        // Need one extra frame beyond the last interpolation base index.
        int sourceFramesNeeded = (int)Math.Ceiling(sourceFramesExact) + 1;
        if (sourceFramesNeeded < 1) sourceFramesNeeded = 1;

        int sourceSamples = sourceFramesNeeded * _outputChannels;
        if (_mixScratch.Length < sourceSamples)
        {
            _mixScratch = new float[Math.Max(sourceSamples, _mixScratch.Length * 2)];
        }
        Array.Clear(_mixScratch, 0, sourceSamples);

        // ===== 2. Per-source: realign late data, peek, mix =====
        int activeCount = 0;
        for (int i = 0; i < sourceCount; i++)
        {
            bool active = IsSourceActiveForThisOutputCore(_sources[i], front, matOutCh);
            _sourceActiveMask[i] = active;
            if (active) activeCount++;
        }
        bool useAll = activeCount == 0;

        int minFramesReadActive = int.MaxValue;
        for (int srcIdx = 0; srcIdx < sourceCount; srcIdx++)
        {
            var src = _sources[srcIdx];
            _framesReadPerSource[srcIdx] = 0;

            // Realign: if this source previously starved, its cursor lags this output's
            // timeline by _pendingSkipPerSource frames. Skip what has since arrived so
            // the cursor returns to the timeline (the late audio is dropped, exactly as
            // it was already replaced by zeros when it failed to arrive on time).
            long pending = _pendingSkipPerSource[srcIdx];
            if (pending > 0)
            {
                int skipped = src.Buffer.SkipForConsumer(_consumerId, (int)Math.Min(pending, int.MaxValue));
                if (skipped > 0)
                {
                    _pendingSkipPerSource[srcIdx] = pending - skipped;
                    if (!string.IsNullOrWhiteSpace(src.DeviceId))
                    {
                        _inputSyncDiscardedFramesByDevice.AddOrUpdate(src.DeviceId, skipped, (_, cur) => cur + skipped);
                    }
                }
            }

            int srcSamples = sourceFramesNeeded * src.Channels;
            if (_sourceTempBuffer.Length < srcSamples)
            {
                _sourceTempBuffer = new float[Math.Max(srcSamples, _sourceTempBuffer.Length * 2)];
            }

            int framesRead = src.Buffer.PeekForConsumer(_consumerId, _sourceTempBuffer, 0, sourceFramesNeeded);
            _framesReadPerSource[srcIdx] = framesRead;

            if (useAll || _sourceActiveMask[srcIdx])
            {
                if (framesRead < minFramesReadActive) minFramesReadActive = framesRead;
            }

            int deficit = sourceFramesNeeded - framesRead;
            int audibleDeficitThreshold = Math.Max(8, _engineSampleRate / 2000); // ~0.5 ms
            if ((useAll || _sourceActiveMask[srcIdx]) && deficit >= audibleDeficitThreshold
                && src.Buffer.GetWritePositionFrames() > 0)
            {
                Interlocked.Increment(ref _underrunCount);
            }

            if (framesRead <= 0) continue;

            // Routing-matrix mix into engine-rate scratch.
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
                        _mixScratch[f * _outputChannels + dstCh] +=
                            _sourceTempBuffer[f * src.Channels + srcCh] * signedGain * muteLinear;
                    }
                }
            }
        }
        if (minFramesReadActive == int.MaxValue) minFramesReadActive = 0;

        // ===== 3. Advance cursors by the timeline amount (truthfully) =====
        // The timeline ALWAYS advances by integerConsume + frac (the device plays this
        // block regardless). Each cursor advances by what its ring could supply; any
        // shortfall is queued as a pending skip so the cursor rejoins the timeline as
        // soon as data arrives.
        for (int srcIdx = 0; srcIdx < sourceCount; srcIdx++)
        {
            var src = _sources[srcIdx];
            int advance = Math.Min(integerConsume, _framesReadPerSource[srcIdx]);
            if (advance > 0)
            {
                src.Buffer.SkipForConsumer(_consumerId, advance);
            }
            int shortfall = integerConsume - advance;
            if (shortfall > 0)
            {
                _pendingSkipPerSource[srcIdx] += shortfall;
            }
        }
        _srcFrac = sourceFramesExact - integerConsume; // in [0,1)

        // ===== 4. Resample engine-rate mix → device-rate output =====
        bool identityPath = _currentTrimPpm == 0
            && _baseRatio == 1.0
            && srcOffsetStart < 1e-12
            && minFramesReadActive >= frames;
        if (identityPath)
        {
            Buffer.BlockCopy(_mixScratch, 0, buffer, offset * sizeof(float), frames * _outputChannels * sizeof(float));
        }
        else
        {
            int outCh = _outputChannels;
            int maxBaseIdx = sourceFramesNeeded - 2;
            if (maxBaseIdx < 0) maxBaseIdx = 0;
            for (int f = 0; f < frames; f++)
            {
                double srcPos = srcOffsetStart + f * effRatio;
                int baseFrame = (int)srcPos;
                double frac = srcPos - baseFrame;
                if (baseFrame > maxBaseIdx) { baseFrame = maxBaseIdx; frac = 1.0; }
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

        for (int i = 0; i < count; i++)
        {
            buffer[offset + i] = ClampSample(buffer[offset + i]);
        }

        ApplyOutputDelay(buffer, offset, count);

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

        // ===== 5. Report exact consumption; receive next block's trim =====
        _currentTrimPpm = _syncCoordinator.ReportConsumedAndUpdate(_consumerId, frames * effRatio, frames);

        long underrunDelta = UnderrunCount - underrunsAtStart;
        if (underrunDelta > 0)
        {
            _syncCoordinator.ReportUnderruns(_consumerId, underrunDelta);
        }

        return count;
    }

    private void EnsureScratchSizes(int sourceCount)
    {
        if (_sourceActiveMask.Length < sourceCount)
        {
            _sourceActiveMask = new bool[Math.Max(sourceCount, _sourceActiveMask.Length * 2)];
        }
        if (_framesReadPerSource.Length < sourceCount)
        {
            _framesReadPerSource = new int[Math.Max(sourceCount, _framesReadPerSource.Length * 2)];
        }
        if (_pendingSkipPerSource.Length < sourceCount)
        {
            var grown = new long[Math.Max(sourceCount, _pendingSkipPerSource.Length * 2)];
            Array.Copy(_pendingSkipPerSource, grown, _pendingSkipPerSource.Length);
            _pendingSkipPerSource = grown;
        }
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
        int min = int.MaxValue;
        bool anyLive = false;
        foreach (var source in _sources)
        {
            // Rings whose producer has never written (capture failed to start, device
            // dead on arrival) must not hold the start barrier hostage or drag the fill
            // measurement to zero forever. Once a ring has produced at least one frame
            // it participates normally.
            if (source.Buffer.GetWritePositionFrames() == 0) continue;
            anyLive = true;
            int available = source.Buffer.GetAvailableFrames(_consumerId);
            if (available < min) min = available;
        }
        if (!anyLive || min == int.MaxValue) return 0;
        return min;
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
            if (_delayBuffer.Length == 0) return;
            for (int i = 0; i < count; i++)
            {
                float delayed = _delayBuffer[_delayWriteIndex];
                _delayBuffer[_delayWriteIndex] = buffer[offset + i];
                buffer[offset + i] = delayed;
                _delayWriteIndex++;
                if (_delayWriteIndex >= _delayBuffer.Length) _delayWriteIndex = 0;
            }
        }
    }
}

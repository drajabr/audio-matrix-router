using System.Threading;

namespace AudioMatrixRouter.Audio;

/// <summary>
/// Capture-side asynchronous sample-rate converter (ASRC).
///
/// Each capture device runs on its own crystal. This stage converts the incoming
/// capture-rate stream into the ENGINE clock domain (the master output's nominal rate)
/// before it enters the shared ring buffer, so that:
///
///   1. Arbitrary input sample rates (44.1k device → 48k engine, etc.) are handled
///      correctly instead of playing at the wrong pitch / draining the ring at huge rates.
///   2. Capture-crystal drift relative to the engine clock is absorbed HERE, by a slow
///      PI servo on ring fill level, instead of accumulating unboundedly in the ring
///      until it overflows or starves (the root cause of the old "fine for minutes,
///      then desyncs" behaviour).
///
/// Control law: error = (ring fill measured at the preferred/master consumer) − target.
/// Fill is a direct measurement, not an estimate, so the loop has a provable equilibrium:
/// if the capture crystal runs fast, fill creeps up, the ratio nudges down, fill returns
/// to target. Trim authority is ±2000 ppm on top of the static rate-conversion ratio —
/// far more than any real crystal mismatch — slew-limited to stay inaudible.
///
/// Threading: ProcessAndWrite is called only from the device's WASAPI capture callback
/// (single-threaded per NAudio). The fill measurement reads the ring under its own lock.
/// </summary>
public sealed class InputAsrc
{
    // Servo tuning. The plant is an integrator (fill = ∫(in-rate − out-rate)), so a PI on
    // fill gives a type-2 loop: zero steady-state error against constant drift.
    private const double MaxTrimPpm = 2000.0;     // covers worst-case crystal mismatch with margin
    private const double SlewPpmPerUpdate = 6.0;  // ~600 ppm/s at 100 callbacks/s — inaudible ramp
    private const double FastSlewPpmPerUpdate = 60.0; // used while |fill error| > ~10 ms (startup/recovery)
    private const double Kp = 0.04;               // ppm per frame of fill error
    private const double Ki = 0.0008;             // ppm per frame·update of integrated error
    private const double IntegralClampFrames = 48000; // ~1 s of integral authority
    private const int DeadbandFrames = 16;        // ignore sub-buffer jitter (~0.33 ms @ 48k)

    private readonly RingBuffer _ring;
    private readonly int _channels;
    private readonly double _baseRatio;       // engineRate / captureRate (static conversion)
    private readonly int _engineSampleRate;
    private readonly double _fastErrorFrames; // |error| beyond this → fast slew

    private int _targetFillFrames;
    private double _hardDrainFrames;
    // Sustained-surplus tracking for the hard drain (~1 s at the 10 ms cadence).
    private const int SurplusWindowUpdates = 100;
    private double _surplusMinFrames = double.MaxValue;
    private int _surplusSamples;
    private double _trimPpm;
    private double _integralFrames;
    // The fill reference consumer. Resolved on every update so it can follow a live
    // master promotion in the coordinator (a dead master's cursor used to pin the
    // servo at -2000 ppm and trigger a hard drain every second on every healthy output).
    private Func<string>? _fillConsumerResolver;
    private string _fillConsumerId = string.Empty;

    // Producer liveness for the idle-resume re-anchor: a source that resumes after a
    // pause (a loopback whose device went quiet, Steam starting a stream into the
    // virtual cable) must rejoin at the target fill, not fight the consumers' pending
    // skips for seconds. 0 = never written since construction.
    private long _lastWriteTicks;
    private double _cadenceMs = 10;
    private static readonly double s_ticksPerMs = System.Diagnostics.Stopwatch.Frequency / 1000.0;

    /// <summary>Number of idle-resume re-anchors performed (telemetry / tests).</summary>
    public long ReanchorCount { get; private set; }

    // Real jitter measurement: peak-to-peak ring-fill excursion observed since the last
    // harvest by the UI. Because the fill servo holds the mean at target, this excursion
    // IS the timing jitter of the capture/render callback interplay — a true measurement,
    // unlike the old UI's |Δlatency-between-polls| sampling noise.
    private int _fillMinFrames = int.MaxValue;
    private int _fillMaxFrames = int.MinValue;
    private readonly object _jitterLock = new();

    // Resampler state: previous block's last frame (for interpolation continuity across
    // callback boundaries) and the fractional source position carried between blocks.
    private readonly float[] _lastFrame;
    private bool _hasLastFrame;
    // Source cursor carried between blocks, measured from the PREVIOUS block's last frame
    // (_lastFrame sits at position -1 of the next block, so the next block starts at
    // s = _srcFrac - 1). Range is (0, step]: exactly 1.0 at unity ratio means "start on
    // capture[0]", and it must NOT be wrapped into [0,1) — doing so re-emitted the
    // previous frame every block (+1 frame per callback = +2083 ppm at 10 ms).
    private double _srcFrac;

    private float[] _outScratch = [];

    public InputAsrc(RingBuffer ring, int channels, int captureSampleRate, int engineSampleRate, int targetFillFrames)
    {
        _ring = ring;
        _channels = Math.Max(1, channels);
        _engineSampleRate = Math.Max(1, engineSampleRate);
        _baseRatio = (double)engineSampleRate / Math.Max(1, captureSampleRate);
        _targetFillFrames = Math.Max(1, targetFillFrames);
        _hardDrainFrames = HardDrainFrames(_targetFillFrames, _engineSampleRate);
        _fastErrorFrames = Math.Max(64, engineSampleRate / 100.0); // ~10 ms
        _lastFrame = new float[_channels];
    }

    /// <summary>Effective ratio currently applied (for telemetry).</summary>
    public double CurrentRatio => _baseRatio * (1.0 + _trimPpm * 1e-6);

    /// <summary>Trim in ppm applied on top of the static rate conversion (for telemetry).</summary>
    public double CurrentTrimPpm => _trimPpm;

    public void SetTargetFillFrames(int frames)
    {
        _targetFillFrames = Math.Max(1, frames);
        _hardDrainFrames = HardDrainFrames(_targetFillFrames, _engineSampleRate);
    }

    /// <summary>
    /// Surplus beyond which the servo stops easing and simply cuts. Ring fill naturally
    /// sawtooths by roughly one render gulp above target (render drains in render-buffer
    /// chunks while capture feeds 10 ms ones), and the budget guarantees target ≥ gulp
    /// + 10 ms — so half a target, floored at 20 ms, always clears the normal excursion
    /// while keeping steady-state latency near what the knob promises. Tighter trips
    /// inside normal operation (a permanent stutter loop, not a recovery).
    /// </summary>
    // A third of a target (min 10 ms). The sustained-minimum trigger makes a tight
    // band safe: it only fires when the FLOOR of the fill stays above threshold for
    // a full second, which normal interleave excursions never do.
    private static double HardDrainFrames(int targetFillFrames, int engineSampleRate) =>
        Math.Max(targetFillFrames / 3.0, engineSampleRate * 10 / 1000.0);

    public void SetFillConsumer(string consumerId)
    {
        _fillConsumerId = consumerId ?? string.Empty;
        _fillConsumerResolver = null;
    }

    /// <summary>Fill reference resolved per update (follows live master promotion).</summary>
    public void SetFillConsumer(Func<string> resolver)
    {
        _fillConsumerResolver = resolver;
    }

    /// <summary>Real delivery cadence of this capture, ms. Drives the idle-resume gap
    /// detection (a gap of more than three cadences is a pause, not jitter).</summary>
    public void SetCadenceMs(double cadenceMs)
    {
        _cadenceMs = cadenceMs > 0 ? cadenceMs : 10;
    }

    private string ResolveFillConsumer()
    {
        var resolver = _fillConsumerResolver;
        if (resolver is not null)
        {
            try { return resolver() ?? string.Empty; } catch { return string.Empty; }
        }
        return _fillConsumerId;
    }

    private int MeasureFill()
    {
        string consumer = ResolveFillConsumer();
        return string.IsNullOrEmpty(consumer)
            ? _ring.AvailableFrames
            : _ring.GetAvailableFrames(consumer);
    }

    /// <summary>
    /// Peak-to-peak ring-fill excursion (ms) since the last call, then resets the window.
    /// Returns null until at least two observations exist. Windowed by the UI's own poll
    /// cadence, so the number shown is exactly "the jitter since you last looked".
    /// </summary>
    public double? GetAndResetFillJitterMs()
    {
        lock (_jitterLock)
        {
            int min = _fillMinFrames;
            int max = _fillMaxFrames;
            _fillMinFrames = int.MaxValue;
            _fillMaxFrames = int.MinValue;

            if (min == int.MaxValue || max == int.MinValue) return null; // no observations yet
            if (max <= min) return 0;
            return Math.Round((max - min) * 1000.0 / _engineSampleRate, 1);
        }
    }

    /// <summary>
    /// Converts one capture callback's worth of interleaved float frames into the engine
    /// clock domain and writes the result into the ring. Returns engine-rate frames written.
    /// </summary>
    public int ProcessAndWrite(float[] capture, int captureFrames)
    {
        if (captureFrames <= 0) return 0;

        // Idle-resume re-anchor. First write after start, or a gap longer than three
        // cadences (min 50 ms): every consumer cursor on this ring moves to
        // writePos - target and that window is zeroed, so the outputs play one target's
        // worth of silence (what they were playing anyway) and then the fresh audio at
        // exactly the configured latency. All consumers move identically, so the
        // output-to-output phase is untouched. Without this a resumed source was
        // skipped block-by-block as "late" and starved for ~5 s, then crackled for ~20 s
        // while the servo rebuilt fill at 2000 ppm.
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_lastWriteTicks == 0 ||
            (now - _lastWriteTicks) / s_ticksPerMs > Math.Max(50.0, 3.0 * _cadenceMs))
        {
            _ring.ReanchorAllConsumers(_targetFillFrames);
            _surplusSamples = 0;
            _surplusMinFrames = double.MaxValue;
            ReanchorCount++;
        }
        _lastWriteTicks = now;

        UpdateServo();

        double ratio = _baseRatio * (1.0 + _trimPpm * 1e-6);
        if (!(ratio > 0)) ratio = _baseRatio;

        // Source timeline for interpolation: index -1 is _lastFrame (previous callback's
        // final frame), indices 0..captureFrames-1 are this block. We may produce output
        // for source positions in [-1 + _srcFrac, captureFrames - 1].
        //
        // Output frame k sits at source position p_k = (_srcFrac - 1) + (k + 1) / ratio
        // measured so that consuming exactly `captureFrames` source frames per block at
        // ratio == exact rate ratio yields a steady stream with no accumulation.
        //
        // Simpler equivalent implementation: walk a source cursor starting at
        // s = _srcFrac - 1 (i.e. inside [_lastFrame, capture[0]]) advancing by 1/ratio
        // per output frame, until s >= captureFrames - 1.
        double step = 1.0 / ratio;
        double s = _srcFrac - 1.0; // position relative to capture[0]; -1 == _lastFrame
        if (!_hasLastFrame)
        {
            // First block ever: start at the first real sample.
            s = 0.0;
        }

        int maxOut = (int)Math.Ceiling((captureFrames - 1 - s) / step) + 2;
        if (maxOut < 1) maxOut = 1;
        int needSamples = maxOut * _channels;
        if (_outScratch.Length < needSamples)
        {
            _outScratch = new float[Math.Max(needSamples, _outScratch.Length * 2)];
        }

        int outFrames = 0;
        while (s <= captureFrames - 1 + 1e-12)
        {
            int i0 = (int)Math.Floor(s);
            double frac = s - i0;

            int outBase = outFrames * _channels;
            if (i0 < 0)
            {
                // Interpolate between _lastFrame and capture[0].
                float fB = (float)frac;
                float fA = 1f - fB;
                for (int ch = 0; ch < _channels; ch++)
                {
                    _outScratch[outBase + ch] = _lastFrame[ch] * fA + capture[ch] * fB;
                }
            }
            else if (i0 >= captureFrames - 1)
            {
                // Exactly on (or numerically at) the final frame.
                int a = (captureFrames - 1) * _channels;
                for (int ch = 0; ch < _channels; ch++)
                {
                    _outScratch[outBase + ch] = capture[a + ch];
                }
            }
            else
            {
                int a = i0 * _channels;
                int b = a + _channels;
                float fB = (float)frac;
                float fA = 1f - fB;
                for (int ch = 0; ch < _channels; ch++)
                {
                    _outScratch[outBase + ch] = capture[a + ch] * fA + capture[b + ch] * fB;
                }
            }

            outFrames++;
            if (outFrames >= maxOut) break; // safety
            s += step;
        }

        // Carry state to the next block: how far past the final source frame the cursor
        // ended up, in (0, step]. Unity ratio lands on exactly 1.0 (= "start the next
        // block on capture[0]"); a step > 1 (capture rate above engine rate) can land
        // further inside the next block. Never wrap it (see the _srcFrac comment).
        double overshoot = s - (captureFrames - 1);
        if (overshoot < 0) overshoot = 0;
        _srcFrac = overshoot;

        int last = (captureFrames - 1) * _channels;
        for (int ch = 0; ch < _channels; ch++)
        {
            _lastFrame[ch] = capture[last + ch];
        }
        _hasLastFrame = true;

        if (outFrames > 0)
        {
            _ring.Write(_outScratch, 0, outFrames);
            TrimBurstBacklog();
        }
        return outFrames;
    }

    /// <summary>
    /// Burst guard, evaluated straight AFTER the write (the servo samples fill at the
    /// top of the block, which is one whole burst too late). A throttled virtual pump —
    /// a Steam/HDMI loopback that only wakes when its device plays — can hand over
    /// seconds of backlog in a couple of callbacks. Left alone that saturates the ring,
    /// and the next writes force-advance every consumer's cursor, which is booked as
    /// huge "dropped frames" spikes even though the audio was never timely. Cutting the
    /// surplus back to target here is a deliberate latency resync (counted in
    /// TotalFramesResynced), not a fault — and it keeps overflow drops for what they
    /// really mean: the ring outran its capacity.
    /// </summary>
    private void TrimBurstBacklog()
    {
        int fill = MeasureFill();

        // Trip well before capacity, and far enough above target that ordinary
        // capture/render interleave never reaches it.
        double capacityGuard = _ring.CapacityFrames - _engineSampleRate / 50.0; // 20 ms of headroom
        double trigger = Math.Min(capacityGuard,
            Math.Max(_ring.CapacityFrames * 0.75, _targetFillFrames + 4 * _hardDrainFrames));
        if (fill > trigger)
        {
            _ring.TrimBacklogBy((int)Math.Min(fill - (double)_targetFillFrames, int.MaxValue));
        }
    }

    private void UpdateServo()
    {
        int fill = MeasureFill();

        lock (_jitterLock)
        {
            if (fill < _fillMinFrames) _fillMinFrames = fill;
            if (fill > _fillMaxFrames) _fillMaxFrames = fill;
        }

        double error = fill - _targetFillFrames; // positive: ring too full → slow down (trim negative)

        // Hard drain, SUSTAINED form. The ±2000 ppm trim removes surplus at 0.2%, so
        // 50 ms of backlog needs ~25 s to clear — but an instantaneous trigger misfired
        // on ordinary burst/render phasing and turned into a stutter loop. Instead:
        // track the rolling MINIMUM surplus over ~1 s; only a floor that stays above
        // the threshold for the whole window is genuine backlog, and cutting by that
        // floor can never bite into fill the interleave actually needs. The integrator
        // is deliberately KEPT — a drain changes fill, not clock drift, and zeroing it
        // discarded the learned crystal offset on every cut.
        if (error < _surplusMinFrames) _surplusMinFrames = error;
        if (++_surplusSamples >= SurplusWindowUpdates)
        {
            if (_surplusMinFrames > _hardDrainFrames)
            {
                _ring.TrimBacklogBy((int)Math.Min(_surplusMinFrames, int.MaxValue));
            }
            _surplusSamples = 0;
            _surplusMinFrames = double.MaxValue;
        }

        double absError = Math.Abs(error);
        double effError = absError <= DeadbandFrames ? 0 : (error > 0 ? error - DeadbandFrames : error + DeadbandFrames);

        // Anti-windup: freeze the integrator when the trim is saturated in the error's direction.
        bool satHi = _trimPpm >= MaxTrimPpm - 1e-9;
        bool satLo = _trimPpm <= -MaxTrimPpm + 1e-9;
        if (!((effError > 0 && satLo) || (effError < 0 && satHi)))
        {
            _integralFrames = Math.Clamp(_integralFrames + effError, -IntegralClampFrames, IntegralClampFrames);
        }

        // Fill too HIGH → capture is producing faster than the engine consumes → trim NEGATIVE
        // (produce fewer engine frames per capture frame). Hence the minus signs.
        double targetTrim = -(Kp * effError + Ki * _integralFrames);
        targetTrim = Math.Clamp(targetTrim, -MaxTrimPpm, MaxTrimPpm);

        double slew = absError > _fastErrorFrames ? FastSlewPpmPerUpdate : SlewPpmPerUpdate;
        double delta = Math.Clamp(targetTrim - _trimPpm, -slew, slew);
        _trimPpm += delta;
    }
}

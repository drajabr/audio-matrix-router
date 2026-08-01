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
    private double _trimPpm;
    private double _integralFrames;
    private string _fillConsumerId = string.Empty;

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
    private double _srcFrac; // in [0,1): fractional offset into the source, measured from _lastFrame

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
    /// Surplus beyond which the servo stops easing and simply cuts. This sets the width
    /// of the residual sawtooth, so it has to stay a fraction of the target: a third of
    /// it, floored at ~6 ms. (A full target's worth let fill ride 45 ms above target on a
    /// ring nothing was draining, which is exactly the "why is it still 100 ms" case.)
    /// </summary>
    private static double HardDrainFrames(int targetFillFrames, int engineSampleRate) =>
        Math.Max(targetFillFrames / 3.0, engineSampleRate * 6 / 1000.0);

    public void SetFillConsumer(string consumerId)
    {
        _fillConsumerId = consumerId ?? string.Empty;
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

        // Carry state to the next block: the new fractional position is how far past the
        // final source frame the cursor ended up (in [0, step)), and _lastFrame becomes
        // this block's final frame.
        double overshoot = s - (captureFrames - 1);
        if (overshoot < 0) overshoot = 0;
        if (overshoot >= 1.0) overshoot -= Math.Floor(overshoot);
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
        }
        return outFrames;
    }

    private void UpdateServo()
    {
        string consumer = _fillConsumerId;
        int fill = string.IsNullOrEmpty(consumer)
            ? _ring.AvailableFrames
            : _ring.GetAvailableFrames(consumer);

        lock (_jitterLock)
        {
            if (fill < _fillMinFrames) _fillMinFrames = fill;
            if (fill > _fillMaxFrames) _fillMaxFrames = fill;
        }

        double error = fill - _targetFillFrames; // positive: ring too full → slow down (trim negative)

        // Hard drain. The ±2000 ppm trim removes surplus at 0.2%, so 50 ms of backlog
        // needs ~25 s of undisturbed running to clear — and a single underrun or restart
        // re-injects more than that. Past a whole extra buffer of backlog the audio is
        // already late by more than the user asked for, so cut it in one step: every
        // consumer advances together, keeping outputs phase-locked to each other.
        if (error > _hardDrainFrames)
        {
            _ring.TrimBacklogTo(_targetFillFrames);
            _integralFrames = 0;
            _trimPpm = 0;
            return; // re-measure next callback rather than servo off a stale fill
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

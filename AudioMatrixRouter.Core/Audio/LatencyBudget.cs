namespace AudioMatrixRouter.Audio;

/// <summary>Inputs of the latency budget, all in ms. Zero/negative values mean "unknown".</summary>
public readonly record struct LatencyBudgetInput(
    /// <summary>The user's LATENCY knob. 0 = AUTO (run at the measured floor).</summary>
    int KnobMs,
    /// <summary>Worst capture delivery cadence across started inputs.</summary>
    double CaptureCadenceMs,
    /// <summary>Worst render engine period across started outputs.</summary>
    double RenderPeriodMs,
    /// <summary>Smallest REAL allocated render buffer across started outputs (0 = not opened yet).</summary>
    int RealRenderBufferMs,
    /// <summary>Headroom the AUTO loop has added on top of the floor.</summary>
    int AutoHeadroomMs,
    /// <summary>Safety tier: extra render periods of padding (0 or 1).</summary>
    int SafetyPeriods);

public readonly record struct LatencyBudgetResult(
    /// <summary>Buffer to request from WASAPI (allocation, not latency).</summary>
    int RenderBufferRequestMs,
    /// <summary>FIFO depth the render loop keeps queued. Uniform across outputs.</summary>
    int RenderPaddingMs,
    /// <summary>Padding + engine period: what the output side really adds.</summary>
    int RenderLatencyMs,
    /// <summary>Lowest ring fill that cannot starve: one render gulp + one capture block + headroom.</summary>
    int RingFloorMs,
    /// <summary>Ring fill target the servos hold.</summary>
    int RingTargetMs,
    /// <summary>cadence + ring target + render latency.</summary>
    int EffectiveTotalMs,
    /// <summary>Total the knob could not go below on this hardware (the AUTO value).</summary>
    int FloorTotalMs,
    /// <summary>True when the needed padding does not fit the allocated buffer: reopen with a bigger request.</summary>
    bool NeedsReallocation,
    bool IsAuto);

/// <summary>
/// The latency budget, as one pure function so it can be unit-tested and logged.
///
/// Everything derives from three measured numbers (capture cadence, render engine
/// period, real render buffer) plus the knob and the AUTO loop's headroom:
///
///   render padding  = what we keep queued in the WASAPI FIFO: one period + 2 ms at the
///                     floor (we top it up by one period per event), more when the knob
///                     or the safety tier asks for it
///   render latency  = padding + the shared engine's own period of mix-ahead
///   ring floor      = padding (one gulp) + cadence (one capture block) + auto headroom
///   ring target     = the floor, or whatever the knob leaves after cadence and render
///   total           = cadence + ring target + render latency
///
/// On 10 ms shared-mode endpoints the floor lands near 54 ms; the old top-the-FIFO
/// loop could not go below ~80 ms whatever the knobs said.
/// </summary>
public static class LatencyBudget
{
    public const int AutoKnob = 0;
    public const int MaxKnobMs = 200;
    public const int MaxAutoHeadroomMs = 40;
    public const int MaxRingTargetMs = 300;
    private const int PaddingMarginMs = 2;

    public static int RenderBufferRequestMs(int knobMs, double renderPeriodMs)
    {
        int period = PeriodMs(renderPeriodMs);
        int twoPeriods = 2 * period;
        if (knobMs <= AutoKnob) return twoPeriods;
        return Math.Clamp(knobMs / 2, twoPeriods, 100);
    }

    public static LatencyBudgetResult Compute(in LatencyBudgetInput i)
    {
        int period = PeriodMs(i.RenderPeriodMs);
        int cadence = (int)Math.Ceiling(i.CaptureCadenceMs > 0 ? i.CaptureCadenceMs : 10);
        int knob = Math.Clamp(i.KnobMs, AutoKnob, MaxKnobMs);
        bool auto = knob <= AutoKnob;
        int safety = Math.Clamp(i.SafetyPeriods, 0, 2);
        int headroom = Math.Clamp(i.AutoHeadroomMs, 0, MaxAutoHeadroomMs);

        int request = RenderBufferRequestMs(knob, period);
        int buffer = i.RealRenderBufferMs > 0 ? i.RealRenderBufferMs : request;

        int paddingMin = period + PaddingMarginMs + safety * period;
        int paddingWanted = auto ? paddingMin : Math.Max(paddingMin, knob / 4);
        bool needsRealloc = paddingMin > buffer;
        int padding = Math.Clamp(paddingWanted, paddingMin, Math.Max(paddingMin, buffer));

        int renderLatency = padding + period;
        int ringFloor = padding + cadence + headroom;
        int ringTarget = auto ? ringFloor : Math.Max(ringFloor, knob - cadence - renderLatency);
        ringTarget = Math.Min(ringTarget, MaxRingTargetMs);

        int total = cadence + ringTarget + renderLatency;
        int floorTotal = cadence + (padding + cadence + headroom) + renderLatency;

        return new LatencyBudgetResult(
            request, padding, renderLatency, ringFloor, ringTarget, total, floorTotal, needsRealloc, auto);
    }

    private static int PeriodMs(double renderPeriodMs) =>
        (int)Math.Ceiling(renderPeriodMs > 0 ? renderPeriodMs : 10);
}

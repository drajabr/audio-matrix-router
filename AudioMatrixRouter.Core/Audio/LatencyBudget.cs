namespace AudioMatrixRouter.Audio;

/// <summary>Inputs of the latency budget, all in ms. Zero/negative values mean "unknown".</summary>
public readonly record struct LatencyBudgetInput(
    /// <summary>The user's LATENCY knob. 0 = AUTO (run at the measured floor).</summary>
    int KnobMs,
    /// <summary>Worst capture delivery cadence across started inputs (MEASURED where available).</summary>
    double CaptureCadenceMs,
    /// <summary>Worst render engine period across started outputs.</summary>
    double RenderPeriodMs,
    /// <summary>Smallest REAL allocated render buffer across started outputs (0 = not opened yet).</summary>
    int RealRenderBufferMs,
    /// <summary>Headroom the AUTO loop has added on top of the floor.</summary>
    int AutoHeadroomMs,
    /// <summary>Safety tier: extra render periods of padding (0 or 1).</summary>
    int SafetyPeriods,
    /// <summary>True when every started output runs an exclusive-mode stream: the driver
    /// double-buffers one period and we fill a whole period per event, so there is no
    /// padding to top up and no margin to add.</summary>
    bool ExclusiveRender = false);

public readonly record struct LatencyBudgetResult(
    /// <summary>Buffer to request from WASAPI (allocation, not latency).</summary>
    int RenderBufferRequestMs,
    /// <summary>FIFO depth the render loop keeps queued. Uniform across outputs.</summary>
    int RenderPaddingMs,
    /// <summary>Padding + engine period: what the output side really adds.</summary>
    int RenderLatencyMs,
    /// <summary>Lowest ring fill that cannot starve: one render gulp + one capture block + headroom.</summary>
    int RingFloorMs,
    /// <summary>Ring fill target the servos hold (for the WORST input; see <see cref="LatencyBudget.RingTargetForCadenceMs"/>).</summary>
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
/// Exclusive mode: the driver double-buffers exactly one period, every event writes a
/// whole period, so padding = period, render latency = 2 × period, and the buffer can
/// never be "too small".
///
/// Per input, the ring target uses THAT input's cadence (<see cref="RingTargetForCadenceMs"/>):
/// a clean 2 ms microphone is not held at the floor a bursty virtual cable needs.
///
/// On 10 ms shared-mode endpoints the floor lands near 54 ms; with 3 ms exclusive
/// endpoints near 19 ms for a hardware input.
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
        int cadence = CadenceMs(i.CaptureCadenceMs);
        int knob = Math.Clamp(i.KnobMs, AutoKnob, MaxKnobMs);
        bool auto = knob <= AutoKnob;
        int safety = Math.Clamp(i.SafetyPeriods, 0, 2);
        int headroom = Math.Clamp(i.AutoHeadroomMs, 0, MaxAutoHeadroomMs);

        int request;
        int padding;
        int renderLatency;
        bool needsRealloc;
        if (i.ExclusiveRender)
        {
            // One period in flight in the driver's second buffer, one being written.
            // The safety tier cannot add padding here (buffer == period); it is simply
            // absorbed into the ring floor below.
            request = period;
            padding = period;
            renderLatency = 2 * period;
            needsRealloc = false;
        }
        else
        {
            request = RenderBufferRequestMs(knob, period);
            int buffer = i.RealRenderBufferMs > 0 ? i.RealRenderBufferMs : request;
            int paddingMin = period + PaddingMarginMs + safety * period;
            int paddingWanted = auto ? paddingMin : Math.Max(paddingMin, knob / 4);
            needsRealloc = paddingMin > buffer;
            padding = Math.Clamp(paddingWanted, paddingMin, Math.Max(paddingMin, buffer));
            renderLatency = padding + period;
        }

        // Exclusive: the FIFO holds exactly one period, so the jitter margin has to live
        // in the ring (as does the safety tier). One render period of margin: at 3 ms
        // gulps a 2 ms scheduling hiccup is most of the fill, and the field run showed
        // a 2 ms margin creeping into underruns within seconds.
        int safetyInRing = i.ExclusiveRender ? safety * period + period : 0;
        int ringFloor = padding + cadence + headroom + safetyInRing;
        int ringTarget = auto ? ringFloor : Math.Max(ringFloor, knob - cadence - renderLatency);
        ringTarget = Math.Min(ringTarget, MaxRingTargetMs);

        int total = cadence + ringTarget + renderLatency;
        int floorTotal = cadence + ringFloor + renderLatency;

        return new LatencyBudgetResult(
            request, padding, renderLatency, ringFloor, ringTarget, total, floorTotal, needsRealloc, auto);
    }

    /// <summary>
    /// Ring target for ONE input given its own cadence, consistent with
    /// <paramref name="budget"/> (computed for the worst input). The knob is an
    /// end-to-end target, so on a manual knob a quieter input keeps its route at the
    /// same total by holding a larger ring; at AUTO it simply runs at its own floor.
    /// </summary>
    public static int RingTargetForCadenceMs(in LatencyBudgetInput i, in LatencyBudgetResult budget, double inputCadenceMs)
    {
        int cadence = CadenceMs(inputCadenceMs);
        int headroom = Math.Clamp(i.AutoHeadroomMs, 0, MaxAutoHeadroomMs);
        int safety = Math.Clamp(i.SafetyPeriods, 0, 2);
        int safetyInRing = i.ExclusiveRender ? (safety + 1) * PeriodMs(i.RenderPeriodMs) : 0;
        int floor = budget.RenderPaddingMs + cadence + headroom + safetyInRing;
        int knob = Math.Clamp(i.KnobMs, AutoKnob, MaxKnobMs);
        int target = budget.IsAuto ? floor : Math.Max(floor, knob - cadence - budget.RenderLatencyMs);
        return Math.Min(target, MaxRingTargetMs);
    }

    private static int PeriodMs(double renderPeriodMs) =>
        (int)Math.Ceiling(renderPeriodMs > 0 ? renderPeriodMs : 10);

    private static int CadenceMs(double cadenceMs) =>
        (int)Math.Ceiling(cadenceMs > 0 ? cadenceMs : 10);
}

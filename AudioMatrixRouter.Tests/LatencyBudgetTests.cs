using AudioMatrixRouter.Audio;
using Xunit;

namespace AudioMatrixRouter.Tests;

public class LatencyBudgetTests
{
    // This machine: 10 ms shared-mode endpoints, Windows hands out a 22 ms buffer for a
    // 20 ms request.
    private static LatencyBudgetInput Typical(int knob, int headroom = 0, int safety = 0) =>
        new(knob, 10, 10, 22, headroom, safety);

    [Fact]
    public void Exclusive_RenderIsTwoPeriods_AndNeverReallocates()
    {
        // 3 ms exclusive endpoints, 3 ms exclusive microphone: buffer == period.
        var b = LatencyBudget.Compute(new LatencyBudgetInput(0, 3, 3, 3, 0, 0, ExclusiveRender: true));
        Assert.Equal(3, b.RenderPaddingMs);
        Assert.Equal(6, b.RenderLatencyMs);
        Assert.Equal(12, b.RingFloorMs);              // gulp 3 + cadence 3 + two periods of jitter margin
        Assert.Equal(21, b.EffectiveTotalMs);         // 3 + 12 + 6
        Assert.False(b.NeedsReallocation);

        // Safety tier cannot pad the exclusive FIFO; it lands in the ring instead.
        var safe = LatencyBudget.Compute(new LatencyBudgetInput(0, 3, 3, 3, 0, 1, ExclusiveRender: true));
        Assert.Equal(3, safe.RenderPaddingMs);
        Assert.Equal(15, safe.RingFloorMs);
        Assert.False(safe.NeedsReallocation);
    }

    [Fact]
    public void MeasuredCadence_RaisesTheFloorHonestly()
    {
        // A virtual cable measured at 17 ms behind its 10 ms period.
        var b = LatencyBudget.Compute(new LatencyBudgetInput(0, 17, 10, 22, 0, 0));
        Assert.Equal(12 + 17, b.RingFloorMs);
        Assert.Equal(17 + 29 + 22, b.EffectiveTotalMs);
    }

    [Fact]
    public void PerInputRingTarget_UsesThatInputsCadence()
    {
        var input = new LatencyBudgetInput(0, 17, 10, 22, 0, 0); // worst input: 17 ms cable
        var b = LatencyBudget.Compute(input);
        Assert.Equal(29, b.RingTargetMs);                                  // worst input
        Assert.Equal(12 + 2, LatencyBudget.RingTargetForCadenceMs(input, b, 2));   // a 2 ms mic beside it
        Assert.Equal(29, LatencyBudget.RingTargetForCadenceMs(input, b, 17));

        // Manual knob: every input lands on the same end-to-end total.
        var manual = new LatencyBudgetInput(120, 17, 10, 22, 0, 0);
        var m = LatencyBudget.Compute(manual);
        int micRing = LatencyBudget.RingTargetForCadenceMs(manual, m, 2);
        Assert.Equal(120, 2 + micRing + m.RenderLatencyMs);
    }

    [Fact]
    public void Auto_RunsAtTheFloor()
    {
        var b = LatencyBudget.Compute(Typical(0));
        Assert.True(b.IsAuto);
        Assert.Equal(12, b.RenderPaddingMs);          // one period + 2 ms
        Assert.Equal(22, b.RenderLatencyMs);          // + engine period
        Assert.Equal(22, b.RingFloorMs);              // gulp 12 + cadence 10
        Assert.Equal(b.RingFloorMs, b.RingTargetMs);
        Assert.Equal(54, b.EffectiveTotalMs);         // 10 + 22 + 22
        Assert.Equal(b.EffectiveTotalMs, b.FloorTotalMs);
        Assert.False(b.NeedsReallocation);
    }

    [Fact]
    public void KnobBelowFloor_SnapsToFloor_KnobAboveFloor_IsHonoured()
    {
        var low = LatencyBudget.Compute(Typical(30));
        Assert.Equal(54, low.EffectiveTotalMs);

        var high = LatencyBudget.Compute(Typical(120));
        Assert.Equal(120, high.EffectiveTotalMs);
        Assert.Equal(22, high.RenderPaddingMs);       // knob/4 = 30 capped by the 22 ms buffer
        Assert.Equal(120 - 10 - 32, high.RingTargetMs);
    }

    [Fact]
    public void Sweep_IsMonotonic_AndNeverRestartsWithinTheBuffer()
    {
        int last = 0;
        for (int knob = 0; knob <= 200; knob += 5)
        {
            var b = LatencyBudget.Compute(Typical(knob));
            Assert.True(b.EffectiveTotalMs >= last, $"knob {knob}: {b.EffectiveTotalMs} < {last}");
            Assert.False(b.NeedsReallocation);
            last = b.EffectiveTotalMs;
        }
    }

    [Fact]
    public void Headroom_AddsToRingOnly_SafetyTier_NeedsBiggerBuffer()
    {
        var h = LatencyBudget.Compute(Typical(0, headroom: 20));
        Assert.Equal(42, h.RingTargetMs);
        Assert.Equal(74, h.EffectiveTotalMs);

        var s = LatencyBudget.Compute(Typical(0, safety: 1));
        Assert.Equal(22, s.RenderPaddingMs);          // two periods + 2 → capped at the 22 ms buffer
        Assert.False(s.NeedsReallocation);            // padding == buffer still leaves a period free per event
        var tiny = LatencyBudget.Compute(new LatencyBudgetInput(0, 10, 10, 11, 0, 1));
        Assert.True(tiny.NeedsReallocation);          // 22 ms needed, 11 ms allocated
    }

    [Fact]
    public void RequestFollowsKnob_WithTwoPeriodMinimum()
    {
        Assert.Equal(20, LatencyBudget.RenderBufferRequestMs(0, 10));
        Assert.Equal(20, LatencyBudget.RenderBufferRequestMs(30, 10));
        Assert.Equal(60, LatencyBudget.RenderBufferRequestMs(120, 10));
        Assert.Equal(100, LatencyBudget.RenderBufferRequestMs(200, 10));
        Assert.Equal(6, LatencyBudget.RenderBufferRequestMs(0, 2.67));
    }
}

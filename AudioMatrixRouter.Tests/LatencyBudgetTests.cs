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

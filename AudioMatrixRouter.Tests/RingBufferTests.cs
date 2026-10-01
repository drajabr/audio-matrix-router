using AudioMatrixRouter.Audio;
using Xunit;

namespace AudioMatrixRouter.Tests;

public class RingBufferTests
{
    [Fact]
    public void Reanchor_PlacesEveryConsumerAtWriteMinusFill_ZeroesWindow_AndBumpsGeneration()
    {
        var ring = new RingBuffer(1024, 2);
        ring.RegisterConsumer("x");
        ring.RegisterConsumer("y");
        var data = new float[200 * 2];
        Array.Fill(data, 0.5f);
        ring.Write(data, 0, 200);
        ring.SkipForConsumer("x", 150); // x is ahead of y

        int genBefore = ring.AnchorGeneration;
        ring.ReanchorAllConsumers(100);

        Assert.Equal(genBefore + 1, ring.AnchorGeneration);
        Assert.Equal(100, ring.GetAvailableFrames("x"));
        Assert.Equal(100, ring.GetAvailableFrames("y"));
        var dest = new float[100 * 2];
        Assert.Equal(100, ring.PeekForConsumer("x", dest, 0, 100));
        Assert.All(dest, s => Assert.Equal(0f, s));
        Assert.Equal(0, ring.GetDroppedFramesForConsumer("x"));
        Assert.Equal(0, ring.TakeForceAdvancedFrames("x"));
    }

    [Fact]
    public void Reanchor_BeforeAnyWrite_YieldsNegativeCursorThatReadsSilence()
    {
        var ring = new RingBuffer(1024, 1);
        ring.RegisterConsumer("x");
        ring.ReanchorAllConsumers(300);
        Assert.Equal(300, ring.GetAvailableFrames("x"));
        var dest = new float[300];
        Assert.Equal(300, ring.ReadForConsumer("x", dest, 0, 300));
        Assert.All(dest, s => Assert.Equal(0f, s));
        Assert.Equal(0, ring.GetAvailableFrames("x"));
    }

    [Fact]
    public void Overflow_ForceAdvancesLaggingConsumer_AndReportsItOnce()
    {
        var ring = new RingBuffer(256, 1);
        ring.RegisterConsumer("lag");
        var data = new float[200];
        ring.Write(data, 0, 200);
        ring.Write(data, 0, 200); // 400 unread exceeds capacity: trimmed
        Assert.True(ring.GetAvailableFrames("lag") <= 255);
        long forced = ring.TakeForceAdvancedFrames("lag");
        Assert.True(forced > 0);
        Assert.Equal(0, ring.TakeForceAdvancedFrames("lag"));
        Assert.Equal(forced, ring.GetDroppedFramesForConsumer("lag"));
    }

    [Fact]
    public void ProducerLiveness_FollowsWrites()
    {
        var ring = new RingBuffer(1024, 1) { ProducerCadenceMs = 10 };
        Assert.False(ring.HasProducerWritten);
        Assert.False(ring.IsProducerLive());
        ring.Write(new float[10], 0, 10);
        Assert.True(ring.HasProducerWritten);
        Assert.True(ring.IsProducerLive());
        Thread.Sleep(120);
        Assert.False(ring.IsProducerLive());      // past three cadences (50 ms floor)
        Assert.True(ring.IsProducerLive(500));
    }
}

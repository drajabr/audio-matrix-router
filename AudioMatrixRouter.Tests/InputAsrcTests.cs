using AudioMatrixRouter.Audio;
using Xunit;

namespace AudioMatrixRouter.Tests;

public class InputAsrcTests
{
    private static (RingBuffer ring, InputAsrc asrc) Make(int captureRate, int engineRate, int channels = 1, int targetFill = 960)
    {
        var ring = new RingBuffer(engineRate, channels); // 1 s capacity
        ring.RegisterConsumer("out");
        var asrc = new InputAsrc(ring, channels, captureRate, engineRate, targetFill);
        asrc.SetFillConsumer("out");
        return (ring, asrc);
    }

    // The fill servo measures the "out" consumer: a test that wants to isolate the
    // resampler must drain "out" by exactly what was produced each block so the fill
    // (hence the trim) stays at zero error. A second consumer keeps everything for
    // content checks.

    [Fact]
    public void UnityRatio_EmitsExactlyOneFramePerInputFrame_AndNoRepeats()
    {
        var (ring, asrc) = Make(48000, 48000);
        ring.RegisterConsumer("probe");
        const int blocks = 50, frames = 480;
        long produced = 0;
        float v = 0;
        var block = new float[frames];
        for (int b = 0; b < blocks; b++)
        {
            for (int i = 0; i < frames; i++) block[i] = v++;
            int n = asrc.ProcessAndWrite(block, frames);
            produced += n;
            ring.SkipForConsumer("out", n);
        }

        Assert.Equal(blocks * frames, produced);

        // The first write re-anchors every consumer at writePos - target, so the first
        // target frames read back are the silence window; after it the ramp is exact.
        int target = 960;
        var dest = new float[blocks * frames + target];
        int got = ring.ReadForConsumer("probe", dest, 0, dest.Length);
        Assert.Equal(blocks * frames + target, got);
        for (int i = 0; i < target; i++) Assert.Equal(0f, dest[i]);
        for (int i = 0; i < blocks * frames; i++)
            Assert.True(Math.Abs(dest[target + i] - i) < 1e-3f, $"frame {i} was {dest[target + i]}: a repeated or skipped sample");
    }

    [Theory]
    [InlineData(44100, 48000, 441, 480)]
    [InlineData(96000, 48000, 960, 480)]
    [InlineData(48000, 44100, 480, 441)]
    public void RateConversion_ConservesFramesOverManyBlocks(int captureRate, int engineRate, int inFrames, int outFrames)
    {
        var (ring, asrc) = Make(captureRate, engineRate);
        const int blocks = 200;
        long produced = 0;
        var block = new float[inFrames];
        for (int b = 0; b < blocks; b++)
        {
            int n = asrc.ProcessAndWrite(block, inFrames);
            produced += n;
            ring.SkipForConsumer("out", n);
        }

        // Within two frames of the ideal over the whole run, not per block: no drift.
        Assert.InRange(produced, (long)blocks * outFrames - 2, (long)blocks * outFrames + 2);
    }

    [Fact]
    public void UnityRatio_ServoHoldsFillAtTarget_WhenConsumerRunsAtEngineRate()
    {
        var (ring, asrc) = Make(48000, 48000, targetFill: 960);
        var block = new float[480];
        for (int b = 0; b < 500; b++)
        {
            asrc.ProcessAndWrite(block, 480);
            ring.SkipForConsumer("out", 480);      // consumer at exactly the engine rate
        }
        // No chronic surplus: the trim must stay inside the deadband-scale noise, never
        // pinned at the cap (the old +1 frame/block bug pinned it at -2000 ppm).
        Assert.InRange(asrc.CurrentTrimPpm, -50, 50);
        Assert.InRange(ring.GetAvailableFrames("out"), 960 - 64, 960 + 64);
    }

    [Fact]
    public void ResumeAfterPause_ReanchorsEveryConsumerAtTargetFill()
    {
        var (ring, asrc) = Make(48000, 48000, channels: 1, targetFill: 480);
        asrc.SetCadenceMs(10);
        ring.RegisterConsumer("a");
        ring.RegisterConsumer("b");
        var block = new float[480];

        asrc.ProcessAndWrite(block, 480);            // first write: anchor 1
        Assert.Equal(1, asrc.ReanchorCount);
        Thread.Sleep(120);                           // a pause longer than three cadences
        asrc.ProcessAndWrite(block, 480);            // anchor 2
        Assert.Equal(2, asrc.ReanchorCount);

        // Both consumers sit exactly target + this block behind the write head.
        Assert.Equal(480 + 480, ring.GetAvailableFrames("a"));
        Assert.Equal(480 + 480, ring.GetAvailableFrames("b"));
        Assert.Equal(ring.GetConsumerPositionFrames("a"), ring.GetConsumerPositionFrames("b"));
    }
}

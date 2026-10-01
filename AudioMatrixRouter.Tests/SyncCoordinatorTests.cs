using AudioMatrixRouter.Audio;
using Xunit;

namespace AudioMatrixRouter.Tests;

public class SyncCoordinatorTests
{
    private static OutputSyncCoordinator Make()
    {
        var c = new OutputSyncCoordinator("m", 48000, 960, 9600);
        c.RegisterConsumer("m", 48000, 40);
        c.RegisterConsumer("f", 48000, 40);
        c.SetReleaseFillTargetMs(20);
        c.ReportBufferedFrames("m", 2000);
        c.ReportBufferedFrames("f", 2000);
        Assert.False(c.ShouldHoldForGlobalRefill());
        return c;
    }

    [Fact]
    public void StalledMaster_IsReplacedByFreshFollower_AndEventFires()
    {
        var c = Make();
        string? promoted = null;
        c.MasterChanged += id => promoted = id;

        for (int i = 0; i < 5; i++)
        {
            c.ReportConsumedAndUpdate("m", 480, 480);
            c.ReportConsumedAndUpdate("f", 480, 480);
            Thread.Sleep(10);
        }
        Assert.Equal("m", c.GetMasterConsumerId());

        Thread.Sleep(350);                           // master goes silent
        c.ReportConsumedAndUpdate("f", 480, 480);    // follower keeps reporting
        Assert.Equal("f", c.GetMasterConsumerId());
        Assert.Equal("f", promoted);
        Assert.Equal(1, c.MasterPromotions);
        Assert.Equal(0, c.ReportConsumedAndUpdate("f", 480, 480)); // master trim is 0
    }

    [Fact]
    public void SingleLateMasterBlock_DoesNotTriggerHardResync()
    {
        var c = Make();
        c.ReportConsumedAndUpdate("m", 480, 480);
        c.ReportConsumedAndUpdate("f", 480, 480);
        c.ReportConsumedAndUpdate("m", 480 * 6, 480 * 6); // one catch-up block
        c.ReportConsumedAndUpdate("f", 480, 480);
        Assert.Equal(0, c.TakeHardResyncFrames("f"));    // needs persistence over 3 blocks
    }

    [Fact]
    public void JoinLive_EntersAtMasterTimeline()
    {
        var c = Make();
        for (int i = 0; i < 10; i++) c.ReportConsumedAndUpdate("m", 480, 480);
        c.RegisterConsumer("late", 48000, 40);
        c.JoinLive("late");
        c.ReportConsumedAndUpdate("m", 480, 480);
        c.ReportConsumedAndUpdate("late", 480, 480);
        Assert.True(Math.Abs(c.GetConsumerSmoothedErrorFrames("late")) < 1000);
        Assert.Equal(0, c.TakeHardResyncFrames("late"));
    }
}

using AudioMatrixRouter.Audio;
using Xunit;

namespace AudioMatrixRouter.Tests;

public class CadenceTrackerTests
{
    [Fact]
    public void RecurringGap_IsReported_OneOffGap_IsNot()
    {
        var t = new CadenceTracker();
        t.RecordGapForTest(10); t.RecordGapForTest(10); t.RecordGapForTest(10);
        t.RecordGapForTest(16); t.RecordGapForTest(16); t.RecordGapForTest(16);
        t.RecordGapForTest(42);                          // one scheduling hiccup
        Assert.Equal(16, t.WorstRecurringGapMs());
        t.RecordGapForTest(42); t.RecordGapForTest(42);  // now it recurs
        Assert.Equal(42, t.WorstRecurringGapMs());
    }

    [Fact]
    public void Empty_ReportsZero()
    {
        var t = new CadenceTracker();
        Assert.Equal(0, t.WorstRecurringGapMs());
    }
}

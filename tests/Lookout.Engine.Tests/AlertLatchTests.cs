using Lookout.Engine.Rules;

namespace Lookout.Engine.Tests;

public class AlertLatchTests
{
    [Fact]
    public void Escalation_is_immediate()
    {
        var latch = new AlertLatch(2);
        Assert.True(latch.Update(AlertLevel.Critical, 0));
        Assert.Equal(AlertLevel.Critical, latch.Reported);
    }

    [Fact]
    public void Downgrade_waits_for_the_hold()
    {
        var latch = new AlertLatch(2);
        latch.Update(AlertLevel.Critical, 0);
        Assert.False(latch.Update(AlertLevel.Warning, 1));
        Assert.False(latch.Update(AlertLevel.Warning, 2.5));
        Assert.True(latch.Update(AlertLevel.Warning, 3));
        Assert.Equal(AlertLevel.Warning, latch.Reported);
    }

    [Fact]
    public void Bouncing_on_a_threshold_sends_nothing()
    {
        var latch = new AlertLatch(2);
        latch.Update(AlertLevel.Critical, 0);
        var changes = new[] { AlertLevel.Warning, AlertLevel.Critical, AlertLevel.Warning, AlertLevel.Critical }
            .Select((l, i) => latch.Update(l, 1 + i * 0.5)).Count(changed => changed);
        Assert.Equal(0, changes);
        Assert.Equal(AlertLevel.Critical, latch.Reported);
    }
}

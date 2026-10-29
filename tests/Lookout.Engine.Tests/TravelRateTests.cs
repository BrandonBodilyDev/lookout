using Lookout.Engine.Travel;

namespace Lookout.Engine.Tests;

/// <summary>Reference values evaluated from Sullivan et al. 2020, Table 2, moderate tertile.</summary>
public class TravelRateTests
{
    [Theory]
    [InlineData(0, 1.38)]
    [InlineData(15, 0.90)]
    [InlineData(30, 0.55)]
    [InlineData(-15, 1.11)]
    public void Moderate_profile_matches_published_fit(double slopeDeg, double expectedMs) =>
        Assert.Equal(expectedMs, TravelRate.SpeedMs(slopeDeg, PaceProfile.Moderate), 2);

    [Fact]
    public void Fastest_travel_is_slightly_downhill()
    {
        var flat = TravelRate.SpeedMs(0);
        Assert.True(TravelRate.SpeedMs(-3) > flat);
        Assert.True(TravelRate.SpeedMs(10) < flat);
    }

    [Fact]
    public void Low_profile_is_slower_than_moderate_on_climbs()
    {
        foreach (var slope in new[] { 0, 10, 20, 30 })
            Assert.True(TravelRate.SpeedMs(slope, PaceProfile.Low) < TravelRate.SpeedMs(slope, PaceProfile.Moderate));
    }
}

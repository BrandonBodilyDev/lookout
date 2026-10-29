using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lookout.Api.Live;

namespace Lookout.Api.Tests;

public sealed class LiveHealthTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 14, 0, 0, TimeSpan.Zero);
    private static readonly LiveOptions Options = new() { TickSeconds = 1 };

    [Fact]
    public void A_runner_that_has_never_ticked_is_not_ready()
    {
        var checks = new LiveHealth().Evaluate(Now, Options);
        Assert.False(checks.Single(c => c.Name == "loop").Ok);
        Assert.False(checks.Single(c => c.Name == "devices").Ok);
    }

    [Fact]
    public void A_stalled_loop_or_a_lost_broker_is_not_ready()
    {
        var connected = true;
        var h = new LiveHealth { DevicesLoaded = true, BrokerConnected = () => connected };
        h.Ticked(Now.AddSeconds(-2));
        Assert.All(h.Evaluate(Now, Options), c => Assert.True(c.Ok, c.Detail));

        // Stalled: no pass for longer than the allowance.
        Assert.False(h.Evaluate(Now + LiveHealth.StallAfter(Options), Options).Single(c => c.Name == "loop").Ok);

        connected = false;
        Assert.False(h.Evaluate(Now, Options).Single(c => c.Name == "broker").Ok);
    }

    [Fact]
    public void Loopback_mode_has_no_broker_check()
    {
        var h = new LiveHealth { DevicesLoaded = true };
        h.Ticked(Now);
        Assert.DoesNotContain(h.Evaluate(Now, Options), c => c.Name == "broker");
    }
}

[Collection("app")]
public sealed class ReadinessEndpointTests(LookoutAppFixture fx)
{
    [Fact]
    public async Task Ready_reports_each_check_and_needs_no_sign_in()
    {
        // The test host runs without the live runner, so only the database is checked.
        var res = await fx.App.CreateClient().GetAsync("/api/health/ready", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("ready", body.GetProperty("status").GetString());
        var db = Assert.Single(body.GetProperty("checks").EnumerateArray());
        Assert.Equal("database", db.GetProperty("name").GetString());
        Assert.True(db.GetProperty("ok").GetBoolean());
    }
}

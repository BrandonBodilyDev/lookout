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
    public void Slowest_pass_covers_only_the_last_minute()
    {
        var h = new LiveHealth();
        Assert.Null(h.SlowestPassMs);
        h.Ticked(Now, 900);
        h.Ticked(Now.AddSeconds(30), 120);
        Assert.Equal(900, h.SlowestPassMs);
        h.Ticked(Now.AddSeconds(61), 80); // the 900 ms pass is now over a minute old
        Assert.Equal(120, h.SlowestPassMs);
    }

    [Fact]
    public void A_flood_of_stray_ids_evicts_strays_in_a_batch_and_keeps_assigned_devices()
    {
        var activity = new DeviceActivity(TimeProvider.System);
        activity.Heard("CREW-DEVICE", 90);
        activity.MarkAssigned("CREW-DEVICE");
        for (var i = 0; i <= DeviceActivity.MaxDevices; i++) activity.Heard($"STRAY-{i}", null);
        Assert.NotNull(activity.Get("CREW-DEVICE"));
        Assert.Null(activity.Get("STRAY-0")); // the oldest strays went
        // One batch trimmed to 90%; the ids heard after it are kept until the next batch.
        Assert.InRange(activity.HeardWithin(TimeSpan.FromHours(1)).Count, DeviceActivity.MaxDevices * 9 / 10, DeviceActivity.MaxDevices * 9 / 10 + 10);
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
    public async Task With_the_runner_disabled_the_api_is_not_ready_to_warn_anyone()
    {
        // This test host has a database but its live runner is off: reachable, yet watching no crew.
        var res = await fx.App.CreateClient().GetAsync("/api/health/ready", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        var checks = (await res.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("checks").EnumerateArray()
            .ToDictionary(c => c.GetProperty("name").GetString()!, c => c.GetProperty("ok").GetBoolean());
        Assert.True(checks["database"]);
        Assert.False(checks["runner"]);
    }
}

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Lookout.Api.Data;
using Lookout.Api.Live;
using Lookout.Link;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Lookout.Api.Tests;

/// <summary>The API with its live runner on, connected to a real Mosquitto broker.</summary>
public sealed class LiveLinkFixture : LookoutAppFixture
{
    private readonly IContainer _broker = new ContainerBuilder("eclipse-mosquitto:2.1.2-alpine")
        .WithCommand("mosquitto", "-c", "/mosquitto-no-auth.conf")
        .WithPortBinding(1883, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("running"))
        .Build();

    public MqttSettings Broker => new() { Host = _broker.Hostname, Port = _broker.GetMappedPublicPort(1883) };

    protected override async ValueTask StartDependenciesAsync() => await _broker.StartAsync();

    /// <summary>Uplinks are not retained, so tests must not publish before the API has subscribed.</summary>
    protected override async ValueTask AfterStartAsync()
    {
        var client = App.CreateClient();
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while ((await client.GetAsync("/api/health/ready")).StatusCode != HttpStatusCode.OK)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The live API did not become ready (database, loop, broker, devices).");
            await Task.Delay(250);
        }
        await Task.Delay(500); // connected is reported before the subscription is acknowledged
    }

    protected override void Configure(IWebHostBuilder b)
    {
        b.UseSetting("Lookout:Live:Enabled", "true");
        b.UseSetting("Lookout:Live:TickSeconds", "0.2");
        b.UseSetting("Lookout:Mqtt:Host", Broker.Host);
        b.UseSetting("Lookout:Mqtt:Port", Broker.Port.ToString());
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _broker.DisposeAsync();
    }
}

[CollectionDefinition("live")]
public sealed class LiveCollection : ICollectionFixture<LiveLinkFixture>;

/// <summary>
/// End to end through the broker: the device registry decides whose reports count and where alerts go.
/// The incident runs at 30x: a scenario minute every 2 real seconds, so COMMS LOST (minute 15) comes
/// after about 30 s, the wind-shift CRITICAL (minute 20) about 10 s later, and the incident closes
/// after about 100 s, well after the test is done.
/// </summary>
[Collection("live")]
public sealed class LiveLinkTests(LiveLinkFixture fx)
{
    [Fact]
    public async Task Uplinks_and_downlinks_follow_the_device_registry()
    {
        var ct = TestContext.Current.CancellationToken;
        var ic = fx.Client("ic");
        var created = await ic.PostAsJsonAsync("/api/incidents", new { scenarioId = "pine-valley-wind-shift", clockRate = 30 }, ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var incident = (await created.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();
        var suffix = incident.ToString("N")[^8..];
        var alphaDevice = $"T-{suffix}-ALPHA";
        var strayDevice = $"T-{suffix}-STRAY";
        Assert.Equal(HttpStatusCode.OK, (await ic.PutAsync($"/api/incidents/{incident}/crews/ALPHA/devices/{alphaDevice}", null, ct)).StatusCode);

        // Play the devices: Alpha's registered one, and a stray nobody assigned.
        var downlinks = new ConcurrentQueue<(string Topic, AlertMessage Alert)>();
        await using var radio = new MqttConnection(fx.Broker, "test-devices", _ => { });
        radio.OnMessage = (topic, payload) => { downlinks.Enqueue((topic, WireFormat.DecodeAlert(payload))); return Task.CompletedTask; };
        await radio.StartAsync([Topics.DeviceDownlink(alphaDevice), Topics.DeviceDownlink(strayDevice)], ct);
        await radio.WaitConnectedAsync(ct).WaitAsync(TimeSpan.FromSeconds(30), ct);
        await Task.Delay(500, ct); // let the subscription settle

        var fix = WireFormat.EncodePosition(new PositionReport(1, DateTimeOffset.UtcNow, 37.359102, -113.62, 80));
        Assert.True(await radio.TryPublishAsync(Topics.DeviceUplink(strayDevice), fix, ct));
        Assert.True(await radio.TryPublishAsync(Topics.DeviceUplink(alphaDevice), fix, ct));

        // Alpha's fix is stored; the stray's is dropped, but the server has heard it.
        await Eventually(async db => await db.CrewFixes.CountAsync(f => f.IncidentId == incident && f.CrewId == "ALPHA", ct) == 1, ct);
        await using (var scope = fx.App.Services.CreateAsyncScope())
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<LookoutDb>().CrewFixes.CountAsync(f => f.IncidentId == incident, ct));
        Assert.Contains(fx.App.Services.GetRequiredService<DeviceActivity>().HeardWithin(TimeSpan.FromMinutes(1)), d => d.DeviceId == strayDevice);

        // With no further fixes Alpha goes COMMS LOST, a warning that needs an acknowledgement. It must
        // reach Alpha's device, and nothing may go to the stray. Acknowledge the alert that is open now,
        // by its link sequence, once its downlink has arrived: an earlier one may already be superseded.
        var deadline = DateTime.UtcNow.AddSeconds(60);
        AlertMessage? warning = null;
        while (warning is null && DateTime.UtcNow < deadline)
        {
            await using (var scope = fx.App.Services.CreateAsyncScope())
            {
                var open = await scope.ServiceProvider.GetRequiredService<LookoutDb>().Alerts.AsNoTracking()
                    .Where(a => a.IncidentId == incident && a.CrewId == "ALPHA" && a.RequiresAck && a.State == Engine.Workflow.AlertState.Sent)
                    .OrderByDescending(a => a.LinkSeq).FirstOrDefaultAsync(ct);
                if (open is not null)
                    warning = downlinks.Where(d => d.Topic == Topics.DeviceDownlink(alphaDevice) && d.Alert.Sequence == (ushort)open.LinkSeq)
                        .Select(d => d.Alert).FirstOrDefault();
            }
            if (warning is null) await Task.Delay(200, ct);
        }
        Assert.NotNull(warning);
        Assert.StartsWith("ALPHA", warning.Text);
        Assert.DoesNotContain(downlinks, d => d.Topic == Topics.DeviceDownlink(strayDevice));

        // The device acknowledges over its uplink; the audit trail names the device.
        Assert.True(await radio.TryPublishAsync(Topics.DeviceUplink(alphaDevice), WireFormat.EncodeAck(new AckReport(warning.Sequence, DateTimeOffset.UtcNow)), ct));
        await Eventually(async db => await db.AuditEvents.AnyAsync(e => e.IncidentId == incident && e.Type == "alert.acknowledged"
            && e.Actor == $"device-{alphaDevice.ToLowerInvariant()}", ct), ct);

        // Bravo has no device: its warnings are raised but never sent on the link.
        await using (var scope = fx.App.Services.CreateAsyncScope())
        {
            var bravo = await scope.ServiceProvider.GetRequiredService<LookoutDb>().Alerts.AsNoTracking()
                .Where(a => a.IncidentId == incident && a.CrewId == "BRAVO" && a.RequiresAck).ToListAsync(ct);
            Assert.NotEmpty(bravo);
            Assert.All(bravo, a => Assert.Null(a.SentAt));
        }
    }

    private async Task Eventually(Func<LookoutDb, Task<bool>> condition, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            await using (var scope = fx.App.Services.CreateAsyncScope())
                if (await condition(scope.ServiceProvider.GetRequiredService<LookoutDb>())) return;
            Assert.True(DateTime.UtcNow < deadline, "Condition not met within 20 s");
            await Task.Delay(200, ct);
        }
    }
}

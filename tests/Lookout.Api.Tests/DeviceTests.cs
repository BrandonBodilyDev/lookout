using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lookout.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Lookout.Api.Tests;

/// <summary>The device registry decides which incident and crew a link device reports for.</summary>
[Collection("app")]
public sealed class DeviceTests(LookoutAppFixture fx)
{
    private async Task<Guid> CreateIncidentAsync()
    {
        var res = await fx.Client("ic").PostAsJsonAsync("/api/incidents", new { scenarioId = "pine-valley-wind-shift" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("id").GetGuid();
    }

    private static string NewDeviceId() => "T-" + Guid.NewGuid().ToString("N")[..12];

    [Fact]
    public async Task Only_the_incident_commander_assigns_devices()
    {
        var ct = TestContext.Current.CancellationToken;
        var incident = await CreateIncidentAsync();
        var url = $"/api/incidents/{incident}/crews/ALPHA/devices/{NewDeviceId()}";
        Assert.Equal(HttpStatusCode.Unauthorized, (await fx.App.CreateClient().PutAsync(url, null, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await fx.Client("observer").PutAsync(url, null, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await fx.Client("crew", "ALPHA", incident).PutAsync(url, null, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await fx.Client("ic").PutAsync(url, null, ct)).StatusCode);
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("has space")]
    [InlineData("plus+sign")]
    [InlineData("hash#")]
    [InlineData("this-device-id-is-far-too-long-for-a-topic")]
    public async Task Invalid_device_ids_are_rejected(string deviceId)
    {
        var ct = TestContext.Current.CancellationToken;
        var incident = await CreateIncidentAsync();
        var res = await fx.Client("ic").PutAsync($"/api/incidents/{incident}/crews/ALPHA/devices/{Uri.EscapeDataString(deviceId)}", null, ct);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Unknown_crew_is_not_found()
    {
        var ct = TestContext.Current.CancellationToken;
        var incident = await CreateIncidentAsync();
        var res = await fx.Client("ic").PutAsync($"/api/incidents/{incident}/crews/ZULU/devices/{NewDeviceId()}", null, ct);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Reassigning_a_device_moves_it_and_both_steps_are_audited()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = await CreateIncidentAsync();
        var second = await CreateIncidentAsync();
        var device = NewDeviceId();
        var ic = fx.Client("ic");

        Assert.Equal(HttpStatusCode.OK, (await ic.PutAsync($"/api/incidents/{first}/crews/alpha/devices/{device}", null, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ic.PutAsync($"/api/incidents/{second}/crews/BRAVO/devices/{device}", null, ct)).StatusCode);

        await using var scope = fx.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LookoutDb>();
        var row = await db.Devices.AsNoTracking().SingleAsync(d => d.DeviceId == device, ct);
        Assert.Equal((second, "BRAVO"), (row.IncidentId, row.CrewId));

        // The incident detail lists the device on its new crew only.
        var detail = await ic.GetFromJsonAsync<JsonElement>($"/api/incidents/{second}", ct);
        var bravo = detail.GetProperty("crews").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "BRAVO");
        Assert.Equal([device], bravo.GetProperty("devices").EnumerateArray().Select(d => d.GetString()));
        var old = await ic.GetFromJsonAsync<JsonElement>($"/api/incidents/{first}", ct);
        Assert.All(old.GetProperty("crews").EnumerateArray(), c => Assert.Empty(c.GetProperty("devices").EnumerateArray()));

        var moved = await db.AuditEvents.AsNoTracking().SingleAsync(e => e.IncidentId == second && e.Type == "device.assigned", ct);
        Assert.Equal(first, moved.Details!.RootElement.GetProperty("from").GetProperty("IncidentId").GetGuid());
        Assert.Equal(1, await db.AuditEvents.CountAsync(e => e.IncidentId == first && e.Type == "device.assigned", ct));
    }

    [Fact]
    public async Task Unassigning_removes_the_device_and_is_audited()
    {
        var ct = TestContext.Current.CancellationToken;
        var incident = await CreateIncidentAsync();
        var device = NewDeviceId();
        var ic = fx.Client("ic");
        Assert.Equal(HttpStatusCode.OK, (await ic.PutAsync($"/api/incidents/{incident}/crews/ALPHA/devices/{device}", null, ct)).StatusCode);

        // Must name the crew it is on.
        Assert.Equal(HttpStatusCode.NotFound, (await ic.DeleteAsync($"/api/incidents/{incident}/crews/BRAVO/devices/{device}", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ic.DeleteAsync($"/api/incidents/{incident}/crews/ALPHA/devices/{device}", ct)).StatusCode);

        await using var scope = fx.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LookoutDb>();
        Assert.False(await db.Devices.AnyAsync(d => d.DeviceId == device, ct));
        Assert.Equal(1, await db.AuditEvents.CountAsync(e => e.IncidentId == incident && e.Type == "device.unassigned", ct));
    }
}

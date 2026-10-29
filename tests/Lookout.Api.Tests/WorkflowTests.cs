using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lookout.Api.Data;
using Lookout.Api.Live;
using Lookout.Engine.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Lookout.Api.Tests;

[Collection("app")]
public sealed class WorkflowTests(LookoutAppFixture fx)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private async Task<Guid> CreateIncidentAsync()
    {
        var res = await fx.Client("ic").PostAsJsonAsync("/api/incidents", new { scenarioId = "pine-valley-wind-shift", clockRate = 4 }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("id").GetGuid();
    }

    /// <summary>Raise an alert the way the live runner does, then mark it sent by the link.</summary>
    private async Task<AlertRecord> RaiseSentAlertAsync(Guid incident, string crew = "ALPHA", AlertLevel level = AlertLevel.Critical)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = fx.App.Services.CreateAsyncScope();
        var alerts = scope.ServiceProvider.GetRequiredService<AlertService>();
        var a = await alerts.RaiseAsync(incident, crew, level, $"{crew} {level}: test", 30, 20, ct);
        await alerts.MarkSentAsync(a.Id, ct);
        return await scope.ServiceProvider.GetRequiredService<LookoutDb>().Alerts.AsNoTracking().SingleAsync(x => x.Id == a.Id, ct);
    }

    [Fact]
    public async Task Roles_gate_incident_creation()
    {
        var ct = TestContext.Current.CancellationToken;
        var body = new { scenarioId = "pine-valley-wind-shift" };
        Assert.Equal(HttpStatusCode.Unauthorized, (await fx.App.CreateClient().PostAsJsonAsync("/api/incidents", body, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await fx.Client("observer").PostAsJsonAsync("/api/incidents", body, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await fx.Client("crew", "ALPHA").PostAsJsonAsync("/api/incidents", body, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await fx.Client("ic").PostAsJsonAsync("/api/incidents", body, ct)).StatusCode);
    }

    [Fact]
    public async Task A_crew_can_only_acknowledge_its_own_alerts()
    {
        var ct = TestContext.Current.CancellationToken;
        var incident = await CreateIncidentAsync();
        var alert = await RaiseSentAlertAsync(incident, "ALPHA");

        var wrongCrew = await fx.Client("crew", "BRAVO").PostAsJsonAsync($"/api/incidents/{incident}/alerts/{alert.Id}/ack", new { version = alert.Version }, ct);
        Assert.Equal(HttpStatusCode.Forbidden, wrongCrew.StatusCode);

        var ownCrew = await fx.Client("crew", "ALPHA").PostAsJsonAsync($"/api/incidents/{incident}/alerts/{alert.Id}/ack", new { version = alert.Version }, ct);
        Assert.Equal(HttpStatusCode.OK, ownCrew.StatusCode);
        Assert.Equal("acknowledged", (await ownCrew.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("state").GetString());
    }

    [Fact]
    public async Task Stale_version_is_rejected_with_the_current_state()
    {
        var ct = TestContext.Current.CancellationToken;
        var incident = await CreateIncidentAsync();
        var alert = await RaiseSentAlertAsync(incident);

        var res = await fx.Client("ic").PostAsJsonAsync($"/api/incidents/{incident}/alerts/{alert.Id}/ack", new { version = alert.Version - 1 }, ct);
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var problem = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal("sent", problem.GetProperty("current").GetProperty("state").GetString());
        Assert.Equal(alert.Version, problem.GetProperty("current").GetProperty("version").GetUInt32());
    }

    [Fact]
    public async Task Two_simultaneous_acknowledgements_one_wins_one_conflicts()
    {
        var ct = TestContext.Current.CancellationToken;
        var incident = await CreateIncidentAsync();
        var alert = await RaiseSentAlertAsync(incident);
        var url = $"/api/incidents/{incident}/alerts/{alert.Id}/ack";

        var results = await Task.WhenAll(
            fx.Client("ic").PostAsJsonAsync(url, new { version = alert.Version }, ct),
            fx.Client("crew", "ALPHA").PostAsJsonAsync(url, new { version = alert.Version }, ct));

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], results.Select(r => r.StatusCode).Order());
        await using var scope = fx.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LookoutDb>();
        Assert.Equal(1, await db.AuditEvents.CountAsync(e => e.AlertId == alert.Id && e.Type == "alert.acknowledged", ct));
    }

    [Fact]
    public async Task Terminal_states_refuse_further_actions()
    {
        var ct = TestContext.Current.CancellationToken;
        var incident = await CreateIncidentAsync();
        var alert = await RaiseSentAlertAsync(incident);
        var ic = fx.Client("ic");

        var cancel = await ic.PostAsJsonAsync($"/api/incidents/{incident}/alerts/{alert.Id}/cancel", new { version = alert.Version, reason = "crew confirmed safe by radio" }, ct);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        var cancelled = await cancel.Content.ReadFromJsonAsync<JsonElement>(ct);

        var ack = await ic.PostAsJsonAsync($"/api/incidents/{incident}/alerts/{alert.Id}/ack", new { version = cancelled.GetProperty("version").GetUInt32() }, ct);
        Assert.Equal(HttpStatusCode.Conflict, ack.StatusCode);
        Assert.Contains("cancelled", (await ack.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Cancelling_needs_a_reason()
    {
        var ct = TestContext.Current.CancellationToken;
        var incident = await CreateIncidentAsync();
        var alert = await RaiseSentAlertAsync(incident);
        var res = await fx.Client("ic").PostAsJsonAsync($"/api/incidents/{incident}/alerts/{alert.Id}/cancel", new { version = alert.Version, reason = " " }, ct);
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task Unacknowledged_warnings_escalate_and_a_new_alert_supersedes_the_old()
    {
        var ct = TestContext.Current.CancellationToken;
        var incident = await CreateIncidentAsync();
        var first = await RaiseSentAlertAsync(incident, "BRAVO", AlertLevel.Warning);

        await using var scope = fx.App.Services.CreateAsyncScope();
        var alerts = scope.ServiceProvider.GetRequiredService<AlertService>();
        var escalated = await alerts.EscalateOverdueAsync(incident, TimeSpan.Zero, ct);
        Assert.Contains(escalated, a => a.Id == first.Id);

        var second = await alerts.RaiseAsync(incident, "BRAVO", AlertLevel.Critical, "BRAVO CRITICAL: test", 25, 21, ct);
        var db = scope.ServiceProvider.GetRequiredService<LookoutDb>();
        var old = await db.Alerts.AsNoTracking().SingleAsync(a => a.Id == first.Id, ct);
        Assert.Equal(Engine.Workflow.AlertState.Superseded, old.State);
        Assert.Equal(second.Id, old.SupersededBy);
        Assert.Equal(first.LinkSeq + 1, second.LinkSeq);
    }

    [Fact]
    public async Task Device_ack_by_link_sequence_is_idempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        var incident = await CreateIncidentAsync();
        var alert = await RaiseSentAlertAsync(incident);
        await using var scope = fx.App.Services.CreateAsyncScope();
        var alerts = scope.ServiceProvider.GetRequiredService<AlertService>();

        Assert.NotNull(await alerts.AcknowledgeFromDeviceAsync(incident, "ALPHA", alert.LinkSeq, ct));
        Assert.Null(await alerts.AcknowledgeFromDeviceAsync(incident, "ALPHA", alert.LinkSeq, ct)); // QoS 1 duplicate
        var db = scope.ServiceProvider.GetRequiredService<LookoutDb>();
        Assert.Equal(1, await db.AuditEvents.CountAsync(e => e.AlertId == alert.Id && e.Type == "alert.ack-ignored", ct));
    }

    [Fact]
    public async Task Audit_trail_cannot_be_edited_or_deleted()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateIncidentAsync();
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync(ct);
        foreach (var sql in new[] { "UPDATE audit_events SET \"Actor\" = 'someone-else'", "DELETE FROM audit_events", "TRUNCATE audit_events" })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            var e = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync(ct));
            Assert.Contains("append-only", e.MessageText);
        }
    }

    [Fact]
    public async Task Audit_and_fix_tables_are_hypertables()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT hypertable_name FROM timescaledb_information.hypertables ORDER BY 1", conn);
        var names = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) names.Add(r.GetString(0));
        Assert.Equal(["audit_events", "crew_fixes"], names);
    }

    [Fact]
    public async Task Route_edits_need_the_current_version_and_must_end_in_the_safety_zone()
    {
        var ct = TestContext.Current.CancellationToken;
        var incident = await CreateIncidentAsync();
        var ic = fx.Client("ic");
        var detail = await ic.GetFromJsonAsync<JsonElement>($"/api/incidents/{incident}", ct);
        var route = detail.GetProperty("routes").EnumerateArray().Single(r => r.GetProperty("id").GetString() == "ER-1");
        var version = route.GetProperty("version").GetUInt32();
        double[][] path = [[-113.6200, 37.3591], [-113.6170, 37.3630], [-113.6137, 37.3672]];

        var stale = await ic.PutAsJsonAsync($"/api/incidents/{incident}/routes/ER-1", new { name = "Alpha new trail", safetyZone = "SZ-1", path, version = version - 1 }, ct);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        double[][] wrongEnd = [[-113.6200, 37.3591], [-113.6170, 37.3630]];
        var outside = await ic.PutAsJsonAsync($"/api/incidents/{incident}/routes/ER-1", new { name = "x", safetyZone = "SZ-1", path = wrongEnd, version }, ct);
        Assert.Equal(HttpStatusCode.Conflict, outside.StatusCode);

        var ok = await ic.PutAsJsonAsync($"/api/incidents/{incident}/routes/ER-1", new { name = "Alpha new trail", safetyZone = "SZ-1", path, version }, ct);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var saved = await ok.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.NotEqual(version, saved.GetProperty("version").GetUInt32());
        // Elevation comes from the scenario terrain, not from the client.
        Assert.All(saved.GetProperty("path").EnumerateArray(), p => Assert.InRange(p[2].GetDouble(), 1500, 1800));

        var observer = await fx.Client("observer").PutAsJsonAsync($"/api/incidents/{incident}/routes/ER-1", new { name = "x", safetyZone = "SZ-1", path, version }, ct);
        Assert.Equal(HttpStatusCode.Forbidden, observer.StatusCode);
    }
}

using System.Text.Json;
using Lookout.Api;
using Lookout.Api.Auth;
using Lookout.Api.Data;
using Lookout.Api.Live;
using Lookout.Api.Tak;
using Lookout.Api.Weather;
using Lookout.Link;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
builder.Services.AddProblemDetails();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(new ScenarioStore(ScenarioStore.Resolve(config["Lookout:ScenarioDir"])));

// Live incidents need PostgreSQL (TimescaleDB + PostGIS). Without a connection string the API
// still serves scenario replays, which is all Phase 0 needed.
var connection = config.GetConnectionString("Lookout");
var live = !string.IsNullOrWhiteSpace(connection);
var devAuth = config.GetValue("Lookout:DevAuth", false);

var jwt = config.GetSection("Lookout:Jwt").Get<JwtSettings>() ?? new JwtSettings();
if (live && jwt.Key.Length < 32)
    throw new InvalidOperationException("Lookout:Jwt:Key must be at least 32 characters when live incidents are enabled.");
builder.Services.AddSingleton(jwt);
builder.Services.AddSingleton<DevTokenIssuer>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.MapInboundClaims = false;
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidIssuer = jwt.Issuer,
        ValidAudience = jwt.Audience,
        IssuerSigningKey = jwt.Key.Length >= 32 ? jwt.SigningKey : null,
        RoleClaimType = "role",
        NameClaimType = "sub",
    };
    // Browsers cannot set headers on WebSocket upgrades; SignalR sends the token in the query string.
    o.Events = new JwtBearerEvents
    {
        OnMessageReceived = ctx =>
        {
            if (ctx.HttpContext.Request.Path.StartsWithSegments("/hubs") && ctx.Request.Query["access_token"] is { Count: > 0 } t)
                ctx.Token = t;
            return Task.CompletedTask;
        },
    };
});
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Policies.Command, p => p.RequireRole(Roles.IncidentCommander))
    .AddPolicy(Policies.Field, p => p.RequireRole(Roles.IncidentCommander, Roles.Crew))
    .AddPolicy(Policies.Read, p => p.RequireRole(Roles.All));

// Traces and metrics. Exported over OTLP only when OTEL_EXPORTER_OTLP_ENDPOINT is set
// (see docker-compose.observability.yml); otherwise they cost almost nothing.
var otlp = !string.IsNullOrWhiteSpace(config["OTEL_EXPORTER_OTLP_ENDPOINT"]);
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(config["OTEL_SERVICE_NAME"] ?? "lookout-api"))
    .WithTracing(t =>
    {
        t.AddSource(Telemetry.Name).AddAspNetCoreInstrumentation().AddNpgsql();
        if (otlp) t.AddOtlpExporter();
    })
    .WithMetrics(m =>
    {
        m.AddMeter(Telemetry.Name).AddAspNetCoreInstrumentation().AddRuntimeInstrumentation();
        if (otlp) m.AddOtlpExporter();
    });

builder.Services.AddHttpClient<NwsClient>(c => c.Timeout = TimeSpan.FromSeconds(15));

if (live)
{
    builder.Services.AddDbContext<LookoutDb>(o => o.UseNpgsql(connection, n => n.UseNetTopologySuite()));
    builder.Services.AddScoped<AlertService>();
    builder.Services.AddScoped<IncidentService>();
    builder.Services.AddSingleton<IncidentSignals>();
    builder.Services.AddSingleton<LiveSnapshots>();
    builder.Services.AddSingleton<DeviceActivity>();
    builder.Services.AddSingleton(config.GetSection("Lookout:Live").Get<LiveOptions>() ?? new LiveOptions());
    // No broker configured means loopback mode: the engine runs, nothing leaves the server.
    builder.Services.AddSingleton(new BrokerConfig(config.GetSection("Lookout:Mqtt").Get<MqttSettings>() is { Host.Length: > 0 } m ? m : null));
    builder.Services.AddSignalR();
    builder.Services.AddSingleton(config.GetSection("Lookout:Cot").Get<CotSettings>() ?? new CotSettings());
    builder.Services.AddSingleton<CotSender>();
    builder.Services.AddSingleton(config.GetSection("Lookout:WebPush").Get<Lookout.Api.Push.WebPushSettings>() ?? new Lookout.Api.Push.WebPushSettings());
    builder.Services.AddHttpClient(nameof(Lookout.Api.Push.PushNotifier), c => c.Timeout = TimeSpan.FromSeconds(10));
    builder.Services.AddSingleton<Lookout.Api.Push.PushNotifier>();
    if (config.GetValue("Lookout:Live:Enabled", true)) builder.Services.AddHostedService<LiveRunner>();
}

var app = builder.Build();
if (devAuth && !app.Environment.IsDevelopment())
    app.Logger.LogWarning("DEVELOPMENT SIGN-IN IS ON in the {Env} environment: anyone who can reach this API can get an incident-commander token. Never expose this deployment.", app.Environment.EnvironmentName);
app.UseAuthentication();
app.UseAuthorization();

if (live && config.GetValue("Lookout:MigrateOnStartup", true))
{
    // Migrations run as the schema owner when a separate connection is configured; requests are then
    // served on the runtime connection, whose role cannot alter tables or rewrite the audit trail.
    var migrations = config.GetConnectionString("LookoutMigrations");
    if (string.IsNullOrWhiteSpace(migrations))
    {
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<LookoutDb>().Database.MigrateAsync();
    }
    else
    {
        await using var owner = new LookoutDb(new DbContextOptionsBuilder<LookoutDb>()
            .UseNpgsql(migrations, n => n.UseNetTopologySuite()).Options);
        await owner.Database.MigrateAsync();
    }
}

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", live, devAuth }));

app.MapGet("/api/scenarios", (ScenarioStore store) =>
    store.List().Select(s => new { s.Id, s.Name, s.Description, s.Start, s.DurationMin }));

app.MapGet("/api/scenarios/{id}/timeline", (string id, ScenarioStore store) =>
    store.Run(id) is { } run ? Results.Ok(TimelineDto.From(run)) : Results.NotFound());

if (live)
{
    app.MapLive(devAuth);
    app.MapHub<IncidentHub>("/hubs/incident");
}

app.Run();

/// <summary>Visible to the integration tests' WebApplicationFactory.</summary>
public partial class Program;

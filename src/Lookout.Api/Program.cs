using System.Text.Json;
using Lookout.Api;
using Lookout.Api.Auth;
using Lookout.Api.Data;
using Lookout.Api.Live;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

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

if (live)
{
    builder.Services.AddDbContext<LookoutDb>(o => o.UseNpgsql(connection, n => n.UseNetTopologySuite()));
    builder.Services.AddScoped<AlertService>();
    builder.Services.AddScoped<IncidentService>();
}

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

if (live && config.GetValue("Lookout:MigrateOnStartup", true))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<LookoutDb>().Database.MigrateAsync();
}

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", live, devAuth }));

app.MapGet("/api/scenarios", (ScenarioStore store) =>
    store.List().Select(s => new { s.Id, s.Name, s.Description, s.Start, s.DurationMin }));

app.MapGet("/api/scenarios/{id}/timeline", (string id, ScenarioStore store) =>
    store.Run(id) is { } run ? Results.Ok(TimelineDto.From(run)) : Results.NotFound());

if (live) app.MapLive(devAuth);

app.Run();

/// <summary>Visible to the integration tests' WebApplicationFactory.</summary>
public partial class Program;

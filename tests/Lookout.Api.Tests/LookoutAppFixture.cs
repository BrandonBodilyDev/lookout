using System.Net.Http.Headers;
using Lookout.Api.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Lookout.Api.Tests;

/// <summary>
/// The real API on a throwaway TimescaleDB + PostGIS container (the same image docker compose uses).
/// The live runner is switched off so tests control every step; the MQTT path is exercised by the
/// field simulator run described in the README.
/// </summary>
public class LookoutAppFixture : IAsyncLifetime
{
    public const string JwtKey = "integration-test-signing-key-0123456789abcdef";

    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder("timescale/timescaledb-ha:pg17.11-ts2.31.0")
        .WithDatabase("lookout")
        .WithUsername("lookout")
        .WithPassword("test")
        .Build();

    public WebApplicationFactory<Program> App { get; private set; } = null!;
    /// <summary>The schema owner's connection (migrations, and tests that inspect the database).</summary>
    public string ConnectionString => _db.GetConnectionString();
    /// <summary>The restricted runtime login the API itself uses, exactly as in docker compose.</summary>
    public string RuntimeConnectionString { get; private set; } = "";
    public string VapidPublicKey { get; private set; } = "";

    /// <summary>Containers a derived fixture needs before the app starts (a broker, say).</summary>
    protected virtual ValueTask StartDependenciesAsync() => ValueTask.CompletedTask;

    /// <summary>Settings on top of the common ones. By default the live runner is off: tests drive the services directly.</summary>
    protected virtual void Configure(IWebHostBuilder b) => b.UseSetting("Lookout:Live:Enabled", "false");

    public async ValueTask InitializeAsync()
    {
        await _db.StartAsync();
        await StartDependenciesAsync();
        // Same init script docker compose uses: a lookout_runtime login in the lookout_app role.
        var init = await File.ReadAllTextAsync(Path.Combine(RepoRoot(), "deploy", "postgres", "020_lookout_runtime.sql"));
        await using (var conn = new Npgsql.NpgsqlConnection(_db.GetConnectionString()))
        {
            await conn.OpenAsync();
            await using var cmd = new Npgsql.NpgsqlCommand(init, conn);
            await cmd.ExecuteNonQueryAsync();
        }
        RuntimeConnectionString = new Npgsql.NpgsqlConnectionStringBuilder(_db.GetConnectionString())
            { Username = "lookout_runtime", Password = "lookout-runtime-dev" }.ConnectionString;

        (VapidPublicKey, var vapidPrivate) = Push.WebPushSettings.Generate();
        App = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.UseSetting("Lookout:WebPush:PublicKey", VapidPublicKey);
            b.UseSetting("Lookout:WebPush:PrivateKey", vapidPrivate);
            b.UseSetting("ConnectionStrings:Lookout", RuntimeConnectionString);
            b.UseSetting("ConnectionStrings:LookoutMigrations", _db.GetConnectionString());
            b.UseSetting("Lookout:DevAuth", "true");
            b.UseSetting("Lookout:Jwt:Key", JwtKey);
            b.UseSetting("Lookout:ScenarioDir", Path.Combine(RepoRoot(), "scenarios"));
            Configure(b);
        });
        _ = App.Server; // starts the host, which applies migrations
    }

    public HttpClient Client(string role, string? crewId = null, Guid? incidentId = null)
    {
        var issuer = App.Services.GetRequiredService<DevTokenIssuer>();
        var client = App.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", issuer.Issue($"{role}-test", role, crewId, incidentId));
        return client;
    }

    public virtual async ValueTask DisposeAsync()
    {
        await App.DisposeAsync();
        await _db.DisposeAsync();
    }

    protected static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Lookout.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("Repo root not found.");
    }
}

[CollectionDefinition("app")]
public sealed class AppCollection : ICollectionFixture<LookoutAppFixture>;

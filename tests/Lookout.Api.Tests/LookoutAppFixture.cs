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
public sealed class LookoutAppFixture : IAsyncLifetime
{
    public const string JwtKey = "integration-test-signing-key-0123456789abcdef";

    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder("timescale/timescaledb-ha:pg17.11-ts2.31.0")
        .WithDatabase("lookout")
        .WithUsername("lookout")
        .WithPassword("test")
        .Build();

    public WebApplicationFactory<Program> App { get; private set; } = null!;
    public string ConnectionString => _db.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await _db.StartAsync();
        App = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.UseSetting("ConnectionStrings:Lookout", _db.GetConnectionString());
            b.UseSetting("Lookout:DevAuth", "true");
            b.UseSetting("Lookout:Jwt:Key", JwtKey);
            b.UseSetting("Lookout:Live:Enabled", "false");
            b.UseSetting("Lookout:ScenarioDir", Path.Combine(RepoRoot(), "scenarios"));
        });
        _ = App.Server; // starts the host, which applies migrations
    }

    public HttpClient Client(string role, string? crewId = null)
    {
        var issuer = App.Services.GetRequiredService<DevTokenIssuer>();
        var client = App.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", issuer.Issue($"{role}-test", role, crewId));
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        await App.DisposeAsync();
        await _db.DisposeAsync();
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Lookout.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("Repo root not found.");
    }
}

[CollectionDefinition("app")]
public sealed class AppCollection : ICollectionFixture<LookoutAppFixture>;

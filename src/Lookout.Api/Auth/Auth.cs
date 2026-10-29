using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Lookout.Api.Auth;

public static class Roles
{
    /// <summary>Incident commander: acknowledges for any crew, cancels alerts, edits routes and zones.</summary>
    public const string IncidentCommander = "ic";
    /// <summary>Read-only view of the incident and its audit trail.</summary>
    public const string Observer = "observer";
    /// <summary>A crew device: acknowledges its own crew's alerts.</summary>
    public const string Crew = "crew";

    public static readonly string[] All = [IncidentCommander, Observer, Crew];
}

public static class Policies
{
    public const string Command = "command";
    public const string Field = "field";
    public const string Read = "read";
}

/// <summary>Who did something, as recorded in the audit trail.</summary>
public sealed record Actor(string Name, string Role, string? CrewId = null)
{
    public static readonly Actor Engine = new("lookout-engine", "system");
    public static readonly Actor Link = new("link-governor", "system");

    public static Actor From(ClaimsPrincipal user) => new(
        user.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? "unknown",
        user.FindFirstValue("role") ?? "unknown",
        user.FindFirstValue("crew_id"));
}

public sealed class JwtSettings
{
    public string Issuer { get; set; } = "lookout";
    public string Audience { get; set; } = "lookout";
    /// <summary>HMAC key, at least 32 bytes. Development only; production would use Entra ID or another OIDC issuer.</summary>
    public string Key { get; set; } = "";

    public SymmetricSecurityKey SigningKey => new(Encoding.UTF8.GetBytes(Key));
}

/// <summary>Issues short-lived tokens for the dashboard and simulated crew devices when dev auth is on.</summary>
public sealed class DevTokenIssuer(JwtSettings settings, TimeProvider clock)
{
    public string Issue(string name, string role, string? crewId = null, TimeSpan? lifetime = null)
    {
        var claims = new List<Claim> { new(JwtRegisteredClaimNames.Sub, name), new("role", role) };
        if (crewId is not null) claims.Add(new Claim("crew_id", crewId));
        var now = clock.GetUtcNow().UtcDateTime;
        var token = new JwtSecurityToken(settings.Issuer, settings.Audience, claims, now, now + (lifetime ?? TimeSpan.FromHours(8)),
            new SigningCredentials(settings.SigningKey, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

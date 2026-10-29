using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using Lookout.Api.Data;
using Lookout.Api.Push;
using Lookout.Engine.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Lookout.Api.Tests;

/// <summary>
/// Web Push without a phone: a fake push service on localhost receives what Chrome's or Apple's
/// would, so the test can check the VAPID authorization, the encrypted body, and pruning of dead
/// subscriptions. Real delivery to a phone has not been tested here.
/// </summary>
[Collection("app")]
public sealed class WebPushTests(LookoutAppFixture fx)
{
    [Fact]
    public async Task Sends_an_encrypted_VAPID_signed_push_and_prunes_gone_subscriptions()
    {
        var ct = TestContext.Current.CancellationToken;
        var port = FreePort();
        var received = new ConcurrentBag<(string Path, Dictionary<string, string> Headers, int BodyLength)>();
        using var fake = new HttpListener();
        fake.Prefixes.Add($"http://127.0.0.1:{port}/");
        fake.Start();
        _ = Task.Run(async () =>
        {
            while (fake.IsListening)
            {
                HttpListenerContext c;
                try { c = await fake.GetContextAsync(); } catch { return; }
                using var ms = new MemoryStream();
                await c.Request.InputStream.CopyToAsync(ms);
                received.Add((c.Request.Url!.AbsolutePath,
                    c.Request.Headers.AllKeys.ToDictionary(k => k!, k => c.Request.Headers[k]!, StringComparer.OrdinalIgnoreCase),
                    (int)ms.Length));
                c.Response.StatusCode = c.Request.Url.AbsolutePath.EndsWith("/gone") ? 410 : 201;
                c.Response.Close();
            }
        }, ct);

        var incident = await CreateIncidentAsync(ct);
        await using (var scope = fx.App.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LookoutDb>();
            db.PushSubscriptions.Add(Subscription(incident, $"http://127.0.0.1:{port}/push/ok"));
            db.PushSubscriptions.Add(Subscription(incident, $"http://127.0.0.1:{port}/push/gone"));
            await db.SaveChangesAsync(ct);
        }

        var notifier = fx.App.Services.GetRequiredService<PushNotifier>();
        var delivered = await notifier.NotifyAsync(incident, "ALPHA", AlertLevel.Critical, "ALPHA CRITICAL: LEAVE NOW via ER-1 to SZ-1", ct);

        Assert.Equal(1, delivered);
        var ok = Assert.Single(received, r => r.Path == "/push/ok");
        Assert.StartsWith("vapid t=", ok.Headers["Authorization"]);
        Assert.Contains($"k={fx.VapidPublicKey}", ok.Headers["Authorization"]);
        Assert.Equal("aes128gcm", ok.Headers["Content-Encoding"]);
        Assert.Equal("high", ok.Headers["Urgency"]);
        Assert.Equal("900", ok.Headers["TTL"]);
        Assert.True(ok.BodyLength > 100, "body should be the encrypted payload");

        await using var check = fx.App.Services.CreateAsyncScope();
        var remaining = await check.ServiceProvider.GetRequiredService<LookoutDb>().PushSubscriptions
            .Where(s => s.IncidentId == incident).Select(s => s.Endpoint).ToListAsync(ct);
        Assert.Equal([$"http://127.0.0.1:{port}/push/ok"], remaining);
        fake.Stop();
    }

    [Fact]
    public async Task Subscribing_requires_an_https_push_endpoint_and_is_idempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        var incident = await CreateIncidentAsync(ct);
        var phone = fx.Client("crew", "ALPHA", incident);
        var (p256dh, auth) = ClientKeys();

        // Only browser push services: anything else would let a token holder make the server call it.
        foreach (var bad in new[] { "http://fcm.googleapis.com/fcm/send/x", "https://internal-admin.local/hook", "https://fcm.googleapis.com.evil.example/x" })
        {
            var res = await phone.PostAsJsonAsync($"/api/incidents/{incident}/crews/ALPHA/push", new { endpoint = bad, keys = new { p256dh, auth } }, ct);
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        }
        var noKeys = await phone.PostAsJsonAsync($"/api/incidents/{incident}/crews/ALPHA/push", new { endpoint = "https://fcm.googleapis.com/fcm/send/x" }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, noKeys.StatusCode);

        var body = new { endpoint = "https://fcm.googleapis.com/fcm/send/abc123", keys = new { p256dh, auth } };
        Assert.Equal(HttpStatusCode.NoContent, (await phone.PostAsJsonAsync($"/api/incidents/{incident}/crews/ALPHA/push", body, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await phone.PostAsJsonAsync($"/api/incidents/{incident}/crews/ALPHA/push", body, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await phone.PostAsJsonAsync($"/api/incidents/{incident}/crews/BRAVO/push", body, ct)).StatusCode);

        await using var scope = fx.App.Services.CreateAsyncScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<LookoutDb>().PushSubscriptions.CountAsync(s => s.IncidentId == incident, ct));
        var key = await fx.App.CreateClient().GetFromJsonAsync<System.Text.Json.JsonElement>("/api/push/public-key", ct);
        Assert.Equal(fx.VapidPublicKey, key.GetProperty("publicKey").GetString());
    }

    private async Task<Guid> CreateIncidentAsync(CancellationToken ct)
    {
        var res = await fx.Client("ic").PostAsJsonAsync("/api/incidents", new { scenarioId = "pine-valley-wind-shift" }, ct);
        return (await res.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(ct)).GetProperty("id").GetGuid();
    }

    /// <summary>A browser-like subscription: a real P-256 public key and a 16-byte auth secret.</summary>
    private static PushSubscriptionRecord Subscription(Guid incident, string endpoint)
    {
        var (p256dh, auth) = ClientKeys();
        return new PushSubscriptionRecord { IncidentId = incident, CrewId = "ALPHA", Endpoint = endpoint, P256dh = p256dh, Auth = auth, CreatedAt = DateTimeOffset.UtcNow };
    }

    private static (string P256dh, string Auth) ClientKeys()
    {
        using var ec = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var q = ec.ExportParameters(false).Q;
        return (WebPushSettings.Base64Url([0x04, .. q.X!, .. q.Y!]), WebPushSettings.Base64Url(RandomNumberGenerator.GetBytes(16)));
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}

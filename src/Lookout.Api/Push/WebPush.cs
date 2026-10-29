using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;
using Lookout.Api.Data;
using Lookout.Engine.Rules;
using Microsoft.EntityFrameworkCore;

namespace Lookout.Api.Push;

/// <summary>VAPID identity for Web Push (RFC 8292). Push is on only when both keys are configured.</summary>
public sealed class WebPushSettings
{
    /// <summary>Uncompressed P-256 public key, base64url (65 bytes decoded).</summary>
    public string? PublicKey { get; set; }
    /// <summary>P-256 private scalar, base64url (32 bytes decoded). A secret.</summary>
    public string? PrivateKey { get; set; }
    /// <summary>Contact for the push service operator: a mailto: or https: URL.</summary>
    public string Subject { get; set; } = "https://github.com/BrandonBodilyDev/lookout";

    public bool Enabled => !string.IsNullOrWhiteSpace(PublicKey) && !string.IsNullOrWhiteSpace(PrivateKey);

    /// <summary>A fresh VAPID key pair.</summary>
    public static (string PublicKey, string PrivateKey) Generate()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = ec.ExportParameters(includePrivateParameters: true);
        var publicKey = new byte[65];
        publicKey[0] = 0x04;
        p.Q.X!.CopyTo(publicKey, 1);
        p.Q.Y!.CopyTo(publicKey, 33);
        return (Base64Url(publicKey), Base64Url(p.D!));
    }

    public static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// Wakes crew phones with Web Push when a warning goes out on the crew link, so the alert arrives even
/// with the page closed (on iPhone, only for the web app added to the home screen). Runs off the live
/// runner's loop. Subscriptions the push service reports gone (404 or 410) are deleted.
/// </summary>
public sealed class PushNotifier(WebPushSettings settings, IServiceScopeFactory scopes, IHttpClientFactory http, ILogger<PushNotifier> log)
{
    public bool Enabled => settings.Enabled;

    public async Task<int> NotifyAsync(Guid incidentId, string crewId, AlertLevel level, string text, CancellationToken ct = default)
    {
        if (!settings.Enabled) return 0;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LookoutDb>();
        var subs = await db.PushSubscriptions.Where(s => s.IncidentId == incidentId && s.CrewId == crewId).ToListAsync(ct);
        if (subs.Count == 0) return 0;

        var client = new PushServiceClient(http.CreateClient(nameof(PushNotifier)))
        {
            DefaultAuthentication = new VapidAuthentication(settings.PublicKey!, settings.PrivateKey!) { Subject = settings.Subject },
        };
        var payload = JsonSerializer.Serialize(new
        {
            title = $"Lookout {level.ToString().ToUpperInvariant()}: {crewId}",
            body = text,
            tag = $"lookout-{incidentId:N}-{crewId}",
            level = level.ToString().ToLowerInvariant(),
            url = "/?mode=crew",
        });

        var delivered = 0;
        foreach (var s in subs)
        {
            var subscription = new PushSubscription { Endpoint = s.Endpoint };
            subscription.SetKey(PushEncryptionKeyName.P256DH, s.P256dh);
            subscription.SetKey(PushEncryptionKeyName.Auth, s.Auth);
            var message = new PushMessage(payload)
            {
                Urgency = level >= AlertLevel.Critical ? PushMessageUrgency.High : PushMessageUrgency.Normal,
                TimeToLive = 15 * 60, // a warning older than 15 minutes is history, not news
                Topic = "lookout-alert",
            };
            try
            {
                await client.RequestPushMessageDeliveryAsync(subscription, message, ct);
                delivered++;
            }
            catch (PushServiceClientException e) when (e.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                db.PushSubscriptions.Remove(s); // the browser unsubscribed or the subscription expired
            }
            catch (Exception e) when (e is PushServiceClientException or HttpRequestException or TaskCanceledException)
            {
                log.LogWarning("Web Push to crew {Crew} failed: {Error}", crewId, e.Message);
            }
        }
        await db.SaveChangesAsync(ct);
        return delivered;
    }
}

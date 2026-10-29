using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using Lookout.Api.Tak;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lookout.Api.Tests;

public class CotSenderTests
{
    [Fact]
    public async Task Streams_events_a_TAK_server_can_parse()
    {
        var ct = TestContext.Current.CancellationToken;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accept = listener.AcceptTcpClientAsync(ct).AsTask();

        await using var sender = new CotSender(new CotSettings { Host = "127.0.0.1", Port = port }, NullLogger<CotSender>.Instance);
        var now = DateTimeOffset.UtcNow;
        var incident = Guid.NewGuid();
        await sender.SendAsync([
            CotBuilder.Crew(incident, "ALPHA", 37.3591, -113.62, "critical", "LEAVE NOW via ER-1 to SZ-1", 87, now),
            CotBuilder.CutoffWarning(incident, "ALPHA", 37.3591, -113.62, "critical", "Fire at route 14:28", now),
        ], ct);

        using var server = await accept;
        var buffer = new byte[16384];
        var text = new StringBuilder();
        var stream = server.GetStream();
        while (!text.ToString().Contains("cutoff-ALPHA"))
        {
            var n = await stream.ReadAsync(buffer, ct);
            if (n == 0) break;
            text.Append(Encoding.UTF8.GetString(buffer, 0, n));
        }
        listener.Stop();

        // TAK reads a stream of <event> documents back to back.
        var events = XElement.Parse($"<stream>{text}</stream>").Elements("event").ToList();
        Assert.Equal(["a-f-G-U-C", "b-m-p-s-p-i"], events.Select(e => (string?)e.Attribute("type")));
    }

    [Fact]
    public async Task An_unreachable_TAK_server_does_not_hold_up_the_caller()
    {
        var ct = TestContext.Current.CancellationToken;
        // 10.255.255.1 is a non-routable address: connects hang until a timeout.
        await using var sender = new CotSender(new CotSettings { Host = "10.255.255.1", Port = 8087 }, NullLogger<CotSender>.Instance);
        var started = DateTimeOffset.UtcNow;
        await sender.SendAsync([CotBuilder.Crew(Guid.NewGuid(), "ALPHA", 37.36, -113.62, "none", null, null, started)], ct);
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(5), "the 3-second limit should cut the connect short");
    }
}

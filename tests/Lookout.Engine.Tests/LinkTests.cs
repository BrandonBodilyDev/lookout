using System.Text;
using Lookout.Link;

namespace Lookout.Engine.Tests;

public class WireFormatTests
{
    [Fact]
    public void Position_report_is_16_bytes_and_round_trips()
    {
        var p = new PositionReport(513, DateTimeOffset.FromUnixTimeSeconds(1_784_066_400), 37.3591023, -113.6200117, 87);
        var bytes = WireFormat.EncodePosition(p);
        Assert.Equal(16, bytes.Length);
        var back = Assert.IsType<PositionReport>(WireFormat.DecodeUplink(bytes));
        Assert.Equal(p.Sequence, back.Sequence);
        Assert.Equal(p.Time, back.Time);
        Assert.Equal(p.Lat, back.Lat, 7);
        Assert.Equal(p.Lon, back.Lon, 7);
        Assert.Equal(87, back.BatteryPercent);
    }

    [Fact]
    public void Ack_is_8_bytes_and_round_trips()
    {
        var a = new AckReport(42, DateTimeOffset.FromUnixTimeSeconds(1_784_066_460));
        var bytes = WireFormat.EncodeAck(a);
        Assert.Equal(8, bytes.Length);
        Assert.Equal(a, WireFormat.DecodeUplink(bytes));
    }

    [Fact]
    public void Alert_round_trips_and_long_text_is_cut_on_a_character_boundary()
    {
        var text = new string('x', 263) + "é and more";   // 'é' is two bytes and straddles the limit
        var bytes = WireFormat.EncodeAlert(new AlertMessage(7, 3, 14 * 60 + 18, text));
        Assert.True(bytes.Length <= WireFormat.MaxDownlinkBytes);
        var back = WireFormat.DecodeAlert(bytes);
        Assert.Equal(7, back.Sequence);
        Assert.Equal(3, back.Level);
        Assert.Equal((ushort)(14 * 60 + 18), back.LeaveByMinuteOfDay);
        Assert.Equal(new string('x', 263), back.Text);
        Assert.True(Encoding.UTF8.GetByteCount(back.Text) <= WireFormat.MaxAlertTextBytes);
    }

    [Fact]
    public void Oversized_or_unknown_uplinks_are_rejected()
    {
        Assert.Throws<FormatException>(() => WireFormat.DecodeUplink(new byte[341]));
        Assert.Throws<FormatException>(() => WireFormat.DecodeUplink(new byte[] { 0x7f, 0, 0 }));
    }

    [Fact]
    public void Topics_parse_back_to_incident_and_path()
    {
        var id = Guid.NewGuid();
        var parsed = Topics.Parse(Topics.Uplink(id, "ALPHA"));
        Assert.NotNull(parsed);
        Assert.Equal(id, parsed.Value.Incident);
        Assert.Equal(["crew", "ALPHA", "up"], parsed.Value.Path);
    }
}

public class LinkGovernorTests
{
    private static readonly byte[] Payload = [WireFormat.TypeAlert, 3, 0, 1, 0xff, 0xff];

    [Fact]
    public void Sends_immediately_then_respects_session_spacing()
    {
        var g = new LinkGovernor(new GovernorSettings(MinSpacingSec: 40, RepeatSec: 60));
        g.Offer("A", Guid.NewGuid(), 2, Payload, requiresAck: true);
        Assert.Single(g.Due(0));

        var newer = Guid.NewGuid();
        g.Offer("A", newer, 3, Payload, requiresAck: true);   // replaces the first
        Assert.Empty(g.Due(30));                               // modem still inside its 40 s spacing
        var sent = Assert.Single(g.Due(40));
        Assert.Equal(newer, sent.AlertId);
        Assert.False(sent.IsRepeat);
    }

    [Fact]
    public void Repeats_until_acknowledged()
    {
        var g = new LinkGovernor(new GovernorSettings(40, 60));
        var id = Guid.NewGuid();
        g.Offer("A", id, 3, Payload, requiresAck: true);
        g.Due(0);
        Assert.Empty(g.Due(59));
        Assert.True(Assert.Single(g.Due(60)).IsRepeat);
        g.Clear("A", id);
        Assert.Empty(g.Due(200));
    }

    [Fact]
    public void Informational_messages_go_once()
    {
        var g = new LinkGovernor(new GovernorSettings(40, 60));
        g.Offer("A", Guid.NewGuid(), 0, Payload, requiresAck: false);
        Assert.Single(g.Due(0));
        Assert.False(g.HasPending("A"));
        Assert.Empty(g.Due(500));
    }

    [Fact]
    public void Most_severe_crew_goes_first()
    {
        var g = new LinkGovernor(new GovernorSettings(40, 60));
        g.Offer("BRAVO", Guid.NewGuid(), 1, Payload, requiresAck: false);
        g.Offer("ALPHA", Guid.NewGuid(), 3, Payload, requiresAck: true);
        Assert.Equal(["ALPHA", "BRAVO"], g.Due(0).Select(t => t.CrewId));
    }
}

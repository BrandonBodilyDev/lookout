using System.Buffers.Binary;
using System.Text;

namespace Lookout.Link;

/// <summary>
/// Byte layouts for the crew link, sized for Iridium Short Burst Data: at most 340 bytes
/// mobile-originated (crew to server) and 270 bytes mobile-terminated (server to crew).
/// Fixed binary layouts, big-endian, no field names on the wire. The crew is identified by the
/// MQTT topic (in SBD, by the modem IMEI), so it is not repeated in the payload.
/// </summary>
public static class WireFormat
{
    public const int MaxUplinkBytes = 340;
    public const int MaxDownlinkBytes = 270;

    public const byte TypePosition = 0x01;
    public const byte TypeAck = 0x02;
    public const byte TypeAlert = 0x10;

    public const int PositionBytes = 16;
    public const int AckBytes = 8;
    public const int AlertHeaderBytes = 6;
    public const int MaxAlertTextBytes = MaxDownlinkBytes - AlertHeaderBytes;

    /// <summary>
    /// 16 bytes: type, battery %, sequence (u16), fix time (u32 Unix seconds),
    /// latitude and longitude (i32, 1e-7 degrees, about 1 cm).
    /// </summary>
    public static byte[] EncodePosition(PositionReport p)
    {
        var b = new byte[PositionBytes];
        b[0] = TypePosition;
        b[1] = (byte)Math.Clamp(p.BatteryPercent, 0, 100);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2), p.Sequence);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), (uint)p.Time.ToUnixTimeSeconds());
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(8), (int)Math.Round(p.Lat * 1e7));
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(12), (int)Math.Round(p.Lon * 1e7));
        return b;
    }

    /// <summary>8 bytes: type, reserved, the alert's link sequence (u16), ack time (u32 Unix seconds).</summary>
    public static byte[] EncodeAck(AckReport a)
    {
        var b = new byte[AckBytes];
        b[0] = TypeAck;
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2), a.AlertSequence);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), (uint)a.Time.ToUnixTimeSeconds());
        return b;
    }

    /// <summary>
    /// Header of 6 bytes (type, level, link sequence u16, leave-by as minutes after local midnight u16
    /// or 0xFFFF) followed by UTF-8 text, truncated on a character boundary to fit 270 bytes.
    /// </summary>
    public static byte[] EncodeAlert(AlertMessage m)
    {
        var text = Truncate(m.Text, MaxAlertTextBytes);
        var b = new byte[AlertHeaderBytes + text.Length];
        b[0] = TypeAlert;
        b[1] = m.Level;
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2), m.Sequence);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(4), m.LeaveByMinuteOfDay ?? ushort.MaxValue);
        text.CopyTo(b, AlertHeaderBytes);
        return b;
    }

    public static IUplink DecodeUplink(ReadOnlySpan<byte> b)
    {
        if (b.Length == 0) throw new FormatException("Empty uplink message.");
        if (b.Length > MaxUplinkBytes) throw new FormatException($"Uplink message is {b.Length} bytes; the limit is {MaxUplinkBytes}.");
        return b[0] switch
        {
            TypePosition when b.Length == PositionBytes => new PositionReport(
                BinaryPrimitives.ReadUInt16BigEndian(b[2..]),
                DateTimeOffset.FromUnixTimeSeconds(BinaryPrimitives.ReadUInt32BigEndian(b[4..])),
                BinaryPrimitives.ReadInt32BigEndian(b[8..]) / 1e7,
                BinaryPrimitives.ReadInt32BigEndian(b[12..]) / 1e7,
                b[1]),
            TypeAck when b.Length == AckBytes => new AckReport(
                BinaryPrimitives.ReadUInt16BigEndian(b[2..]),
                DateTimeOffset.FromUnixTimeSeconds(BinaryPrimitives.ReadUInt32BigEndian(b[4..]))),
            _ => throw new FormatException($"Unknown uplink type 0x{b[0]:x2} or wrong length {b.Length}."),
        };
    }

    public static AlertMessage DecodeAlert(ReadOnlySpan<byte> b)
    {
        if (b.Length < AlertHeaderBytes || b[0] != TypeAlert) throw new FormatException("Not an alert message.");
        if (b.Length > MaxDownlinkBytes) throw new FormatException($"Alert is {b.Length} bytes; the limit is {MaxDownlinkBytes}.");
        var leaveBy = BinaryPrimitives.ReadUInt16BigEndian(b[4..]);
        return new AlertMessage(
            BinaryPrimitives.ReadUInt16BigEndian(b[2..]),
            b[1],
            leaveBy == ushort.MaxValue ? null : leaveBy,
            Encoding.UTF8.GetString(b[AlertHeaderBytes..]));
    }

    /// <summary>UTF-8 bytes of <paramref name="text"/>, cut so no multi-byte character is split.</summary>
    public static byte[] Truncate(string text, int maxBytes)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length <= maxBytes) return bytes;
        var cut = maxBytes;
        while (cut > 0 && (bytes[cut] & 0xC0) == 0x80) cut--;
        return bytes[..cut];
    }
}

public interface IUplink;

public sealed record PositionReport(ushort Sequence, DateTimeOffset Time, double Lat, double Lon, int BatteryPercent) : IUplink;

public sealed record AckReport(ushort AlertSequence, DateTimeOffset Time) : IUplink;

/// <param name="Level">0 none, 1 watch, 2 warning, 3 critical (same order as the engine's AlertLevel).</param>
public sealed record AlertMessage(ushort Sequence, byte Level, ushort? LeaveByMinuteOfDay, string Text);

/// <summary>MQTT topic layout, after OwnTracks: one tree per incident, one branch per crew.</summary>
public static class Topics
{
    public static string Uplink(Guid incident, string crewId) => $"lookout/{incident}/crew/{crewId}/up";
    public static string Downlink(Guid incident, string crewId) => $"lookout/{incident}/crew/{crewId}/down";
    public static string DroneFront(Guid incident) => $"lookout/{incident}/drone/front";
    public static string Weather(Guid incident) => $"lookout/{incident}/wx";

    public const string AllUplinks = "lookout/+/crew/+/up";
    public const string AllDroneFronts = "lookout/+/drone/front";
    public const string AllWeather = "lookout/+/wx";

    /// <summary>Parses lookout/{incident}/{kind}/... into its parts.</summary>
    public static (Guid Incident, string[] Path)? Parse(string topic)
    {
        var parts = topic.Split('/');
        if (parts.Length < 3 || parts[0] != "lookout" || !Guid.TryParse(parts[1], out var id)) return null;
        return (id, parts[2..]);
    }
}

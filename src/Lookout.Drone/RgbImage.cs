using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Lookout.Drone;

/// <summary>8-bit RGB image, row-major, top row first.</summary>
public sealed class RgbImage(int width, int height, byte[]? pixels = null)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte[] Pixels { get; } = pixels ?? new byte[width * height * 3];

    public (byte R, byte G, byte B) this[int x, int y]
    {
        get { var i = (y * Width + x) * 3; return (Pixels[i], Pixels[i + 1], Pixels[i + 2]); }
        set { var i = (y * Width + x) * 3; Pixels[i] = value.R; Pixels[i + 1] = value.G; Pixels[i + 2] = value.B; }
    }

    public static RgbImage ReadPng(string path) => Png.Decode(File.ReadAllBytes(path));
    public void WritePng(string path) => File.WriteAllBytes(path, Png.Encode(this));
}

/// <summary>
/// Minimal PNG codec: 8-bit RGB or RGBA, non-interlaced. That covers ffmpeg's PNG frame output
/// and Terrarium elevation tiles, without a native imaging dependency.
/// </summary>
public static class Png
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static RgbImage Decode(byte[] data)
    {
        if (!data.AsSpan(0, 8).SequenceEqual(Signature)) throw new InvalidDataException("Not a PNG file.");
        int pos = 8, width = 0, height = 0, channels = 0;
        using var idat = new MemoryStream();
        while (pos < data.Length)
        {
            var len = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(pos));
            var type = Encoding.ASCII.GetString(data, pos + 4, 4);
            var body = pos + 8;
            if (type == "IHDR")
            {
                width = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(body));
                height = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(body + 4));
                if (data[body + 8] != 8 || data[body + 12] != 0) throw new NotSupportedException("Only 8-bit, non-interlaced PNG.");
                channels = data[body + 9] switch { 2 => 3, 6 => 4, _ => throw new NotSupportedException("Only RGB or RGBA PNG.") };
            }
            else if (type == "IDAT") idat.Write(data, body, len);
            else if (type == "IEND") break;
            pos = body + len + 4;
        }

        idat.Position = 0;
        using var z = new ZLibStream(idat, CompressionMode.Decompress);
        var stride = width * channels;
        var raw = new byte[height * (stride + 1)];
        var read = 0;
        while (read < raw.Length) { var n = z.Read(raw, read, raw.Length - read); if (n == 0) break; read += n; }

        var img = new RgbImage(width, height);
        var cur = new byte[stride];
        var prev = new byte[stride];
        for (var y = 0; y < height; y++)
        {
            var filter = raw[y * (stride + 1)];
            Array.Copy(raw, y * (stride + 1) + 1, cur, 0, stride);
            for (var x = 0; x < stride; x++)
            {
                int a = x >= channels ? cur[x - channels] : 0, b = prev[x], c = x >= channels ? prev[x - channels] : 0;
                cur[x] = (byte)(cur[x] + filter switch
                {
                    0 => 0, 1 => a, 2 => b, 3 => (a + b) / 2, 4 => Paeth(a, b, c),
                    _ => throw new InvalidDataException($"Bad PNG filter {filter}."),
                });
            }
            for (var x = 0; x < width; x++)
                Buffer.BlockCopy(cur, x * channels, img.Pixels, (y * width + x) * 3, 3);
            (prev, cur) = (cur, prev);
        }
        return img;
    }

    public static byte[] Encode(RgbImage img)
    {
        var stride = img.Width * 3;
        var raw = new byte[img.Height * (stride + 1)];
        for (var y = 0; y < img.Height; y++)
            Buffer.BlockCopy(img.Pixels, y * stride, raw, y * (stride + 1) + 1, stride); // filter 0 (none)

        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw);

        using var png = new MemoryStream();
        png.Write(Signature);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, img.Width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), img.Height);
        ihdr[8] = 8; ihdr[9] = 2;
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", compressed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] body)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, body.Length);
        s.Write(len);
        var typed = new byte[4 + body.Length];
        Encoding.ASCII.GetBytes(type, typed);
        body.CopyTo(typed, 4);
        s.Write(typed);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typed));
        s.Write(crc);
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc32(byte[] data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}

using System.IO.Compression;
using Lookout.Engine.Geo;
using Lookout.Engine.Terrain;

namespace Lookout.Sim;

/// <summary>
/// Builds a scenario terrain grid from Terrarium elevation tiles (AWS Open Data "Terrain Tiles",
/// https://registry.opendata.aws/terrain-tiles/; USGS 3DEP in the US). Terrarium encodes height as
/// h = R * 256 + G + B / 256 - 32768 metres. Run once per scenario; the grid is committed.
/// </summary>
public static class TerrariumDem
{
    private const string TileUrl = "https://s3.amazonaws.com/elevation-tiles-prod/terrarium/{0}/{1}/{2}.png";
    private const int Zoom = 14;
    private const int TileSize = 256;

    public static async Task<TerrainGrid> BuildAsync(LocalFrame frame, double west, double east, double south, double north, double cellSize)
    {
        var nx = (int)Math.Round((east - west) / cellSize);
        var ny = (int)Math.Round((north - south) / cellSize);
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("lookout-dem/1.0 (+https://github.com/BrandonBodilyDev/lookout)");
        var tiles = new Dictionary<(int, int), byte[,,]>();

        async Task<byte[,,]> Tile(int tx, int ty)
        {
            if (tiles.TryGetValue((tx, ty), out var t)) return t;
            var png = await http.GetByteArrayAsync(string.Format(TileUrl, Zoom, tx, ty));
            return tiles[(tx, ty)] = Png.DecodeRgb(png);
        }

        async Task<double> Height(double px, double py)
        {
            var tx = (int)Math.Floor(px / TileSize);
            var ty = (int)Math.Floor(py / TileSize);
            var rgb = await Tile(tx, ty);
            int x = (int)px - tx * TileSize, y = (int)py - ty * TileSize;
            return rgb[y, x, 0] * 256.0 + rgb[y, x, 1] + rgb[y, x, 2] / 256.0 - 32768.0;
        }

        var z = new double[nx * ny];
        for (var j = 0; j < ny; j++)
        for (var i = 0; i < nx; i++)
        {
            var c = new NetTopologySuite.Geometries.Coordinate(west + (i + 0.5) * cellSize, south + (j + 0.5) * cellSize);
            var (lon, lat) = frame.ToLonLat(c);
            var (px, py) = WebMercatorPixel(lon, lat);
            // Bilinear over the four nearest tile pixels.
            double fx = px - 0.5, fy = py - 0.5;
            int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
            double ax = fx - x0, ay = fy - y0;
            var h00 = await Height(x0, y0); var h10 = await Height(x0 + 1, y0);
            var h01 = await Height(x0, y0 + 1); var h11 = await Height(x0 + 1, y0 + 1);
            z[j * nx + i] = (h00 * (1 - ax) + h10 * ax) * (1 - ay) + (h01 * (1 - ax) + h11 * ax) * ay;
        }
        Console.WriteLine($"Fetched {tiles.Count} tiles at z{Zoom}; grid {nx} x {ny} at {cellSize} m.");
        return new TerrainGrid(nx, ny, west, south, cellSize, z);
    }

    private static (double X, double Y) WebMercatorPixel(double lon, double lat)
    {
        var n = TileSize * Math.Pow(2, Zoom);
        var latRad = lat * Math.PI / 180;
        return ((lon + 180) / 360 * n, (1 - Math.Log(Math.Tan(latRad) + 1 / Math.Cos(latRad)) / Math.PI) / 2 * n);
    }

    /// <summary>Minimal PNG decoder: 8-bit RGB or RGBA, non-interlaced, which is what Terrarium tiles use.</summary>
    private static class Png
    {
        public static byte[,,] DecodeRgb(byte[] data)
        {
            int pos = 8, width = 0, height = 0, channels = 0;
            using var idat = new MemoryStream();
            while (pos < data.Length)
            {
                var len = (data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3];
                var type = System.Text.Encoding.ASCII.GetString(data, pos + 4, 4);
                var body = pos + 8;
                if (type == "IHDR")
                {
                    width = (data[body] << 24) | (data[body + 1] << 16) | (data[body + 2] << 8) | data[body + 3];
                    height = (data[body + 4] << 24) | (data[body + 5] << 16) | (data[body + 6] << 8) | data[body + 7];
                    if (data[body + 8] != 8 || data[body + 12] != 0) throw new NotSupportedException("Only 8-bit, non-interlaced PNG.");
                    channels = data[body + 9] switch { 2 => 3, 6 => 4, _ => throw new NotSupportedException("Only RGB/RGBA PNG.") };
                }
                else if (type == "IDAT") idat.Write(data, body, len);
                else if (type == "IEND") break;
                pos = body + len + 4; // skip CRC
            }

            idat.Position = 0;
            using var z = new ZLibStream(idat, CompressionMode.Decompress);
            var stride = width * channels;
            var raw = new byte[height * (stride + 1)];
            var read = 0;
            while (read < raw.Length) { var n = z.Read(raw, read, raw.Length - read); if (n == 0) break; read += n; }

            var cur = new byte[stride];
            var prev = new byte[stride];
            var outp = new byte[height, width, 3];
            for (var y = 0; y < height; y++)
            {
                var filter = raw[y * (stride + 1)];
                Array.Copy(raw, y * (stride + 1) + 1, cur, 0, stride);
                for (var x = 0; x < stride; x++)
                {
                    int a = x >= channels ? cur[x - channels] : 0, b = prev[x], c = x >= channels ? prev[x - channels] : 0;
                    cur[x] = (byte)(cur[x] + filter switch
                    {
                        0 => 0, 1 => a, 2 => b, 3 => (a + b) / 2,
                        4 => Paeth(a, b, c),
                        _ => throw new InvalidDataException($"Bad PNG filter {filter}."),
                    });
                }
                for (var x = 0; x < width; x++)
                    for (var k = 0; k < 3; k++) outp[y, x, k] = cur[x * channels + k];
                (prev, cur) = (cur, prev);
            }
            return outp;
        }

        private static int Paeth(int a, int b, int c)
        {
            int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }
    }
}

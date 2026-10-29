using System.Globalization;
using System.Text;
using NetTopologySuite.Geometries;

namespace Lookout.Engine.Terrain;

/// <summary>
/// Ground elevation on a regular grid in the scenario's local metric frame (x east, y north).
/// Stored on disk as an ESRI ASCII grid whose xllcorner / yllcorner are local-frame metres, not
/// lon/lat. Cell (i, j) has its centre at (X0 + (i + 0.5) * CellSize, Y0 + (j + 0.5) * CellSize),
/// with j = 0 the southernmost row.
/// </summary>
public sealed class TerrainGrid
{
    public TerrainGrid(int nx, int ny, double x0, double y0, double cellSize, double[] z)
    {
        if (z.Length != nx * ny) throw new ArgumentException("Elevation array does not match grid size.", nameof(z));
        Nx = nx; Ny = ny; X0 = x0; Y0 = y0; CellSize = cellSize; Z = z;
    }

    public int Nx { get; }
    public int Ny { get; }
    public double X0 { get; }
    public double Y0 { get; }
    public double CellSize { get; }
    public double[] Z { get; }

    public int Index(int i, int j) => j * Nx + i;
    public double At(int i, int j) => Z[Index(i, j)];
    public Coordinate Centre(int i, int j) => new(X0 + (i + 0.5) * CellSize, Y0 + (j + 0.5) * CellSize);
    public Envelope Bounds => new(X0, X0 + Nx * CellSize, Y0, Y0 + Ny * CellSize);

    /// <summary>The cell containing (x, y), clamped to the grid edge.</summary>
    public (int I, int J) CellOf(double x, double y) =>
        (Math.Clamp((int)Math.Floor((x - X0) / CellSize), 0, Nx - 1),
         Math.Clamp((int)Math.Floor((y - Y0) / CellSize), 0, Ny - 1));

    /// <summary>Bilinear elevation between cell centres.</summary>
    public double Elevation(double x, double y)
    {
        var fx = Math.Clamp((x - X0) / CellSize - 0.5, 0, Nx - 1);
        var fy = Math.Clamp((y - Y0) / CellSize - 0.5, 0, Ny - 1);
        var i = Math.Min((int)fx, Nx - 2);
        var j = Math.Min((int)fy, Ny - 2);
        var tx = fx - i;
        var ty = fy - j;
        var a = At(i, j) * (1 - tx) + At(i + 1, j) * tx;
        var b = At(i, j + 1) * (1 - tx) + At(i + 1, j + 1) * tx;
        return a * (1 - ty) + b * ty;
    }

    /// <summary>A flat grid, for tests and for scenarios without terrain.</summary>
    public static TerrainGrid Flat(Envelope bounds, double cellSize, double elevation = 0)
    {
        var nx = (int)Math.Ceiling(bounds.Width / cellSize);
        var ny = (int)Math.Ceiling(bounds.Height / cellSize);
        return new TerrainGrid(nx, ny, bounds.MinX, bounds.MinY, cellSize, Enumerable.Repeat(elevation, nx * ny).ToArray());
    }

    public static TerrainGrid ReadAscii(string path)
    {
        using var reader = new StreamReader(path);
        var header = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        for (var k = 0; k < 6; k++)
        {
            var parts = reader.ReadLine()!.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            header[parts[0]] = double.Parse(parts[1], CultureInfo.InvariantCulture);
        }
        int nx = (int)header["ncols"], ny = (int)header["nrows"];
        var z = new double[nx * ny];
        // ASCII grids list the northernmost row first.
        for (var row = 0; row < ny; row++)
        {
            var values = reader.ReadLine()!.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var j = ny - 1 - row;
            for (var i = 0; i < nx; i++) z[j * nx + i] = double.Parse(values[i], CultureInfo.InvariantCulture);
        }
        return new TerrainGrid(nx, ny, header["xllcorner"], header["yllcorner"], header["cellsize"], z);
    }

    public void WriteAscii(string path)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"ncols {Nx}\nnrows {Ny}\nxllcorner {X0}\nyllcorner {Y0}\ncellsize {CellSize}\nNODATA_value -9999\n");
        for (var j = Ny - 1; j >= 0; j--)
        {
            for (var i = 0; i < Nx; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(At(i, j).ToString("0.0", CultureInfo.InvariantCulture));
            }
            sb.Append('\n');
        }
        File.WriteAllText(path, sb.ToString());
    }
}

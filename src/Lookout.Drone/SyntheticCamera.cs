using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;
using NetTopologySuite.Operation.Distance;

namespace Lookout.Drone;

/// <summary>
/// Renders what a camera would see of a scenario's fire: sky above the horizon, vegetation, a
/// flaming band along the fire edge, and dark burn scar inside it, with deterministic texture noise.
/// It exists so the detect-and-project pipeline can be scored against the scenario's ground truth
/// without real footage. It is a test instrument, not a realistic image synthesizer.
/// </summary>
public sealed class SyntheticCamera(GroundProjector projector, double flameBandM = 25)
{
    public RgbImage Render(CameraPose pose, Geometry burned, int seed = 1)
    {
        var cam = projector.Camera;
        var img = new RgbImage(cam.Width, cam.Height);
        var prepared = burned.IsEmpty ? null : PreparedGeometryFactory.Prepare(burned);
        var boundary = burned.IsEmpty ? null : burned.Boundary;
        var factory = burned.Factory;

        for (var v = 0; v < cam.Height; v++)
        for (var u = 0; u < cam.Width; u++)
        {
            var n = Noise(u, v, seed);
            var ground = projector.PixelToGround(u + 0.5, v + 0.5, pose);
            if (ground is null) { img[u, v] = Shade((150, 185, 220), n, 0.1); continue; }

            var p = factory.CreatePoint(ground);
            if (prepared is not null && prepared.Contains(p))
            {
                var edgeDistance = DistanceOp.Distance(boundary!, p);
                img[u, v] = edgeDistance <= flameBandM
                    ? Shade((235, 110 + (int)(60 * n), 25), n, 0.15)   // flame
                    : Shade((42, 40, 37), n, 0.4);                      // burn scar
            }
            else
            {
                // Pinyon-juniper and dry grass: patchy greens and tans.
                var patch = Noise((int)(ground.X / 6), (int)(ground.Y / 6), seed + 7);
                img[u, v] = patch > 0.55 ? Shade((86, 104, 70), n, 0.25) : Shade((168, 154, 112), n, 0.25);
            }
        }
        return img;
    }

    /// <summary>Base colour with brightness varied by +/- spread/2 according to noise n in [0, 1).</summary>
    private static (byte, byte, byte) Shade((int R, int G, int B) c, double n, double spread)
    {
        var f = 1 + (n - 0.5) * spread;
        return ((byte)Math.Clamp(c.R * f, 0, 255), (byte)Math.Clamp(c.G * f, 0, 255), (byte)Math.Clamp(c.B * f, 0, 255));
    }

    /// <summary>Deterministic hash noise in [0, 1).</summary>
    private static double Noise(int x, int y, int seed)
    {
        unchecked
        {
            var h = (uint)(x * 374761393 + y * 668265263 + seed * 2147483647);
            h = (h ^ (h >> 13)) * 1274126177;
            h ^= h >> 16;
            return (h & 0xFFFFFF) / (double)0x1000000;
        }
    }
}

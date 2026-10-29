using Lookout.Engine.Terrain;
using NetTopologySuite.Geometries;

namespace Lookout.Drone;

/// <summary>Pinhole camera: image size and field of view.</summary>
public sealed record CameraModel(int Width, int Height, double HfovDeg, double VfovDeg)
{
    /// <summary>
    /// Original Mavic Mini, 1080p video. 83° diagonal and a 24 mm equivalent lens on a 1/2.3" sensor
    /// give about 71.5° horizontal; 44° vertical assumes 16:9 video uses the full sensor width.
    /// Derived from the spec sheet, not calibrated: check against a ground target of known size.
    /// </summary>
    public static CameraModel MavicMini1080p { get; } = WithAspect(1920, 1080, 71.5);

    public static CameraModel WithAspect(int width, int height, double hfovDeg)
    {
        var vfov = 2 * Math.Atan(Math.Tan(hfovDeg * Math.PI / 360) * height / width) * 180 / Math.PI;
        return new CameraModel(width, height, hfovDeg, vfov);
    }

    public double Fx => Width / (2 * Math.Tan(HfovDeg * Math.PI / 360));
    public double Fy => Height / (2 * Math.Tan(VfovDeg * Math.PI / 360));
}

/// <summary>
/// Where the camera is and where it points, in the scenario's local frame.
/// </summary>
/// <param name="Position">Drone position, metres east/north of the scenario origin.</param>
/// <param name="AltitudeM">Camera altitude above sea level, metres.</param>
/// <param name="HeadingDeg">Direction the camera faces, degrees true.</param>
/// <param name="PitchDeg">Gimbal pitch: 0 is the horizon, -90 straight down.</param>
public sealed record CameraPose(Coordinate Position, double AltitudeM, double HeadingDeg, double PitchDeg);

/// <summary>
/// Pixel to ground. Standard direct georeferencing with a pinhole model: build the pixel's ray in
/// the camera frame (x right, y down, z forward), rotate it by gimbal pitch and heading into
/// east-north-up, and intersect it with the ground. Over terrain the intersection is iterated:
/// intersect with the plane at the last ground height, look up the terrain there, repeat. Rays
/// within about 6 degrees of the horizon are rejected; their ground error is unbounded.
/// </summary>
public sealed class GroundProjector(CameraModel camera, TerrainGrid? terrain)
{
    private const double MinDownward = 0.1;
    private const int Iterations = 6;

    public CameraModel Camera => camera;

    /// <summary>World direction (east, north, up) of the ray through pixel (u, v).</summary>
    public (double E, double N, double U) Ray(double u, double v, CameraPose pose)
    {
        var x = (u - camera.Width / 2.0) / camera.Fx;
        var y = (v - camera.Height / 2.0) / camera.Fy;
        var len = Math.Sqrt(x * x + y * y + 1);
        x /= len; y /= len; var z = 1 / len;

        var psi = pose.HeadingDeg * Math.PI / 180;
        var theta = pose.PitchDeg * Math.PI / 180;
        // Forward, right, and image-down axes in east-north-up.
        double fE = Math.Sin(psi) * Math.Cos(theta), fN = Math.Cos(psi) * Math.Cos(theta), fU = Math.Sin(theta);
        double rE = Math.Cos(psi), rN = -Math.Sin(psi), rU = 0;
        // up = right x forward; image down = -up.
        double upE = rN * fU - rU * fN, upN = rU * fE - rE * fU, upU = rE * fN - rN * fE;
        return (x * rE - y * upE + z * fE, x * rN - y * upN + z * fN, x * rU - y * upU + z * fU);
    }

    /// <summary>Ground point seen at pixel (u, v), or null if the ray does not reach the ground.</summary>
    public Coordinate? PixelToGround(double u, double v, CameraPose pose)
    {
        var (e, n, up) = Ray(u, v, pose);
        if (up > -MinDownward) return null;
        var groundZ = terrain?.Elevation(pose.Position.X, pose.Position.Y) ?? 0;
        double gx = pose.Position.X, gy = pose.Position.Y;
        for (var i = 0; i < Iterations; i++)
        {
            var h = pose.AltitudeM - groundZ;
            if (h <= 0) return null;
            var t = h / -up;
            gx = pose.Position.X + t * e;
            gy = pose.Position.Y + t * n;
            if (terrain is null) break;
            var next = terrain.Elevation(gx, gy);
            if (Math.Abs(next - groundZ) < 0.5) break;
            groundZ = next;
        }
        return new Coordinate(gx, gy);
    }
}

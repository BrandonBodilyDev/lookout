namespace Lookout.Engine.Geo;

/// <summary>Compass bearings: degrees clockwise from true north, in the local x-east / y-north frame.</summary>
public static class Bearing
{
    public static double ToRadians(double degrees) => degrees * Math.PI / 180.0;

    public static double Normalize(double degrees) => ((degrees % 360.0) + 360.0) % 360.0;

    /// <summary>Unit vector (dx, dy) pointing along a compass bearing.</summary>
    public static (double Dx, double Dy) UnitVector(double bearingDeg)
    {
        var r = ToRadians(bearingDeg);
        return (Math.Sin(r), Math.Cos(r));
    }

    /// <summary>Compass bearing of the vector (dx, dy).</summary>
    public static double Of(double dx, double dy) => Normalize(Math.Atan2(dx, dy) * 180.0 / Math.PI);

    /// <summary>Smallest absolute angle between two bearings, 0..180.</summary>
    public static double Difference(double a, double b)
    {
        var d = Math.Abs(Normalize(a) - Normalize(b));
        return d > 180.0 ? 360.0 - d : d;
    }

    /// <summary>Wind is reported as the direction it blows FROM; fire heads the opposite way.</summary>
    public static double HeadDirectionFromWind(double windFromDeg) => Normalize(windFromDeg + 180.0);
}

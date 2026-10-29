using Lookout.Engine.Geo;
using Lookout.Engine.Scenarios;
using Lookout.Engine.Spread;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Distance;

namespace Lookout.Engine.Rules;

/// <summary>
/// Watch Out Situation #11, "unburned fuel between you and fire" (2025 IRPG numbering): the crew is
/// outside the burned area, close to the front, and downwind of it, so the fire's head is pointed
/// at them across fuel that has not burned. Phase 0 has no fuel map, so "unburned" means outside
/// the burned polygon.
/// </summary>
public static class UnburnedFuelRule
{
    public const string Id = "WO11-UNBURNED-FUEL";

    public static Finding? Evaluate(Coordinate crew, Geometry front, SpreadConditions wind, AlertSettings settings)
    {
        if (front.IsEmpty) return null;
        var point = front.Factory.CreatePoint(crew);
        var distance = front.Distance(point);
        if (distance <= 0 || distance > settings.UnburnedFuelDistanceM) return null;

        var nearest = DistanceOp.NearestPoints(front, point)[0];
        var bearingToCrew = Bearing.Of(crew.X - nearest.X, crew.Y - nearest.Y);
        var head = Bearing.HeadDirectionFromWind(wind.WindFromDeg);
        if (Bearing.Difference(bearingToCrew, head) > settings.UnburnedFuelHalfAngleDeg) return null;

        return new Finding(Id, AlertLevel.Watch,
            $"Watch Out #11: crew downwind of the fire with unburned fuel between, {distance:0} m");
    }
}

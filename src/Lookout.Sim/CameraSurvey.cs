using Lookout.Drone;
using Lookout.Engine.Scenarios;
using NetTopologySuite.Geometries;

namespace Lookout.Sim;

/// <summary>Replays a scenario with the drone pipeline in the loop: render, detect, map, accumulate.</summary>
public static class CameraSurvey
{
    public static Func<double, Geometry> Observer(Scenario scenario, double altitudeM = 1800)
    {
        var terrain = scenario.Terrain ?? throw new InvalidOperationException("Camera surveys need a scenario with terrain.");
        var pts = scenario.Routes.SelectMany(r => r.Vertices).ToList();
        var fire0 = scenario.Fire.FrontAt(0).Centroid;
        var centre = new Coordinate((pts.Average(p => p.X) + fire0.X) / 2, (pts.Average(p => p.Y) + fire0.Y) / 2 - 300);
        var projector = new GroundProjector(CameraModel.WithAspect(800, 600, 71.5), terrain);
        var camera = new SyntheticCamera(projector);
        var detector = new ColorFireDetector();
        var mapper = new FrontMapper(terrain, projector);
        var pose = new CameraPose(centre, terrain.Elevation(centre.X, centre.Y) + altitudeM, 0, -90);
        var seed = 0;
        return t =>
        {
            mapper.Add(detector.Detect(camera.Render(pose, scenario.Fire.FrontAt(t), ++seed)), pose);
            return mapper.Front();
        };
    }
}

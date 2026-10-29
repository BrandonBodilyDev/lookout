using Lookout.Drone;
using NetTopologySuite.Geometries;

/// <summary>Virtual survey: photograph the true fire, detect it, accumulate what was detected.</summary>
sealed record SurveyRig(SyntheticCamera Camera, ColorFireDetector Detector, FrontMapper Mapper, CameraPose Pose)
{
    public Geometry Survey(Geometry truth, int seed)
    {
        var frame = Camera.Render(Pose, truth, seed);
        Mapper.Add(Detector.Detect(frame), Pose);
        return Mapper.Front();
    }
}

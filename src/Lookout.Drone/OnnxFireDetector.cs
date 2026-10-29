using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Lookout.Drone;

/// <summary>
/// Runs an ONNX object detector (DETR family, such as RF-DETR or D-FINE exported to ONNX) and turns
/// its boxes into a fire mask. Inference stays in .NET; Python is only needed to export the model.
///
/// Expected graph, as produced by RF-DETR's ONNX export: one image input [1, 3, H, W], RGB scaled
/// to 0-1 and normalised with ImageNet mean and standard deviation; one output of boxes
/// [1, N, 4] as (cx, cy, w, h) normalised to 0-1; one output of class logits [1, N, C].
/// The input size is read from the model. Graph handling, thresholding and box-to-pixel mapping are
/// tested against a tiny model with this interface (tools/make_test_detector.py). Real RF-DETR
/// weights have not been run in this repo: check the input and output shapes your export prints.
///
/// Recommended weights are Apache-2.0 (for example fireviewer/rfdetr-medium-probable-fire-v2.5).
/// Ultralytics YOLO weights are AGPL-3.0 and would change this project's licence.
/// </summary>
public sealed class OnnxFireDetector : IFireDetector, IDisposable
{
    private static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] Std = [0.229f, 0.224f, 0.225f];

    private readonly InferenceSession _session;
    private readonly string _input;
    private readonly int _w, _h;
    private readonly float _threshold;
    private readonly HashSet<int>? _classes;

    /// <param name="fireClasses">Class indices that mean fire or smoke; null accepts every class.</param>
    public OnnxFireDetector(string modelPath, float threshold = 0.4f, IEnumerable<int>? fireClasses = null, bool useCoreMl = false)
    {
        var options = new SessionOptions();
        if (useCoreMl) options.AppendExecutionProvider_CoreML();
        _session = new InferenceSession(modelPath, options);
        var input = _session.InputMetadata.First();
        _input = input.Key;
        var dims = input.Value.Dimensions;
        _h = dims[2] > 0 ? dims[2] : 640;
        _w = dims[3] > 0 ? dims[3] : 640;
        _threshold = threshold;
        _classes = fireClasses?.ToHashSet();
        Name = $"ONNX {Path.GetFileName(modelPath)} ({_w}x{_h})";
    }

    public string Name { get; }

    public FireMask Detect(RgbImage frame)
    {
        var tensor = new DenseTensor<float>([1, 3, _h, _w]);
        for (var y = 0; y < _h; y++)
        for (var x = 0; x < _w; x++)
        {
            // Nearest-neighbour resize is enough for a detector input.
            var (r, g, b) = frame[x * frame.Width / _w, y * frame.Height / _h];
            tensor[0, 0, y, x] = (r / 255f - Mean[0]) / Std[0];
            tensor[0, 1, y, x] = (g / 255f - Mean[1]) / Std[1];
            tensor[0, 2, y, x] = (b / 255f - Mean[2]) / Std[2];
        }

        using var results = _session.Run([NamedOnnxValue.CreateFromTensor(_input, tensor)]);
        var outputs = results.Select(r => r.AsTensor<float>()).ToList();
        var boxes = outputs.First(t => t.Dimensions[^1] == 4);
        var logits = outputs.First(t => !ReferenceEquals(t, boxes));

        var mask = new FireMask(frame.Width, frame.Height);
        var n = boxes.Dimensions[1];
        var classes = logits.Dimensions[2];
        for (var i = 0; i < n; i++)
        {
            var best = -1; var bestScore = 0f;
            for (var c = 0; c < classes; c++)
            {
                var score = 1f / (1f + MathF.Exp(-logits[0, i, c]));
                if (score > bestScore) { bestScore = score; best = c; }
            }
            if (bestScore < _threshold || (_classes is not null && !_classes.Contains(best))) continue;

            float cx = boxes[0, i, 0], cy = boxes[0, i, 1], bw = boxes[0, i, 2], bh = boxes[0, i, 3];
            var x0 = (int)Math.Clamp((cx - bw / 2) * frame.Width, 0, frame.Width - 1);
            var x1 = (int)Math.Clamp((cx + bw / 2) * frame.Width, 0, frame.Width - 1);
            var y0 = (int)Math.Clamp((cy - bh / 2) * frame.Height, 0, frame.Height - 1);
            var y1 = (int)Math.Clamp((cy + bh / 2) * frame.Height, 0, frame.Height - 1);
            for (var y = y0; y <= y1; y++) for (var x = x0; x <= x1; x++) mask[x, y] = true;
        }
        return mask;
    }

    public void Dispose() => _session.Dispose();
}

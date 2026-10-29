namespace Lookout.Drone;

/// <summary>Per-pixel fire mask for one frame: true where the detector sees fire or fresh burn scar.</summary>
public sealed class FireMask(int width, int height)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public bool[] Bits { get; } = new bool[width * height];
    public bool this[int x, int y] { get => Bits[y * Width + x]; set => Bits[y * Width + x] = value; }
    public int Count => Bits.Count(b => b);
}

public interface IFireDetector
{
    string Name { get; }
    FireMask Detect(RgbImage frame);
}

/// <summary>
/// Rule-based colour detector, no trained weights: a baseline that runs on any footage, including
/// your own fire-pit flight. A flame pixel must pass two published rule sets:
/// Chen, Wu and Chiou (2004), "An early fire-detection method based on image processing" (ICIP):
/// R &gt; R_T, R &gt;= G &gt; B, and S &gt;= (255 - R) * S_T / R_T, with R_T = 135 and S_T = 60
/// (S on a 0-255 scale); and the chrominance rule of Celik and Demirel (2009), "Fire detection in
/// video sequences using a generic color model" (Fire Safety Journal): Cr &gt; Cb and |Cr - Cb| &gt;= 40.
/// Chen's rule alone accepts dry tan grass, which is most of the ground in desert scrub; the
/// chrominance rule rejects it. Celik's frame-mean rules are left out (they assume fire is a small
/// part of the frame, which is not true of an aerial survey over a large fire).
/// Fresh burn scar is dark and unsaturated (V &lt; 70, S &lt; 70) and only counts next to flame or
/// other scar, which keeps shadows from being mapped as burned ground. Small specks are dropped.
/// Colour rules confuse sunsets, red vehicles and autumn leaves; a trained detector does better.
/// </summary>
public sealed class ColorFireDetector(int minRegionPixels = 12) : IFireDetector
{
    private const int RT = 135, ST = 60;

    public string Name => "colour rules (Chen et al. 2004)";

    private const double ChromaGap = 40;

    public static bool IsFlame(byte r, byte g, byte b)
    {
        if (r <= RT || r < g || g <= b) return false;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var s = max == 0 ? 0 : (max - min) * 255.0 / max;
        if (s < (255.0 - r) * ST / RT) return false;
        // ITU-R BT.601 chrominance.
        var cb = 128 - 0.168736 * r - 0.331264 * g + 0.5 * b;
        var cr = 128 + 0.5 * r - 0.418688 * g - 0.081312 * b;
        return cr - cb >= ChromaGap;
    }

    public static bool IsScar(byte r, byte g, byte b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var s = max == 0 ? 0 : (max - min) * 255.0 / max;
        return max < 70 && s < 70;
    }

    public FireMask Detect(RgbImage frame)
    {
        int w = frame.Width, h = frame.Height;
        var kind = new byte[w * h]; // 0 none, 1 flame, 2 scar candidate
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var (r, g, b) = frame[x, y];
            kind[y * w + x] = IsFlame(r, g, b) ? (byte)1 : IsScar(r, g, b) ? (byte)2 : (byte)0;
        }

        // Keep connected regions (4-neighbour) that contain flame and are big enough.
        var mask = new FireMask(w, h);
        var seen = new bool[w * h];
        var stack = new Stack<int>();
        var region = new List<int>();
        for (var start = 0; start < w * h; start++)
        {
            if (kind[start] == 0 || seen[start]) continue;
            region.Clear();
            var hasFlame = false;
            stack.Push(start);
            seen[start] = true;
            while (stack.Count > 0)
            {
                var p = stack.Pop();
                region.Add(p);
                hasFlame |= kind[p] == 1;
                int px = p % w, py = p / w;
                foreach (var q in new[] { px > 0 ? p - 1 : -1, px < w - 1 ? p + 1 : -1, py > 0 ? p - w : -1, py < h - 1 ? p + w : -1 })
                {
                    if (q < 0 || seen[q] || kind[q] == 0) continue;
                    seen[q] = true;
                    stack.Push(q);
                }
            }
            if (!hasFlame || region.Count < minRegionPixels) continue;
            foreach (var p in region) mask.Bits[p] = true;
        }
        return mask;
    }
}

using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace UltraKey.Core.Ai;

/// <summary>
/// Subject segmentation for photos with arbitrary backgrounds, using the embedded U²-Net model.
/// Produces a soft subject mask at full resolution; the keyer then refines its edges on the real pixels.
/// The network result is cached per image, so changing other settings does not run it again.
/// </summary>
public static class AiSegmenter
{
    private const string ResourceName = "UltraKey.u2net.ukm";
    private static readonly Lazy<U2Net?> Model = new(LoadModel, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly object CacheSync = new();
    private static (int w, int h, ulong hash, float[] mask)? cache;

    public static bool IsAvailable => Model.Value != null;

    private static U2Net? LoadModel()
    {
        Assembly asm = typeof(AiSegmenter).Assembly;
        using Stream? embedded = asm.GetManifestResourceStream(ResourceName);
        if (embedded != null) return U2Net.Load(embedded);
        string? dir = Path.GetDirectoryName(asm.Location);
        string file = Path.Combine(dir ?? ".", "u2net.ukm");
        if (File.Exists(file))
        {
            using var fs = File.OpenRead(file);
            return U2Net.Load(fs);
        }
        return null;
    }

    /// <summary>Returns the subject probability (0..1) for every pixel, or null when the model is missing.</summary>
    public static float[]? Segment(uint[] px, int w, int h, CancellationToken ct)
    {
        ulong hash = Hash(px, w, h);
        lock (CacheSync)
        {
            if (cache is { } c && c.w == w && c.h == h && c.hash == hash) return c.mask;
        }
        U2Net? net = Model.Value;
        if (net == null) return null;

        int s = net.InputSize;
        float[] input = Prepare(px, w, h, s);
        float[] map = net.Run(input, ct);

        float mn = float.MaxValue, mx = float.MinValue;
        foreach (float v in map)
        {
            if (v < mn) mn = v;
            if (v > mx) mx = v;
        }
        float inv = mx > mn ? 1f / (mx - mn) : 0f;
        var mask = new float[w * h];
        float fx = (float)s / w, fy = (float)s / h;
        Parallel.For(0, h, ImageOps.Options(ct), y =>
        {
            float sy = MathF.Max(0f, (y + 0.5f) * fy - 0.5f);
            int y0 = Math.Min(s - 1, (int)sy), y1 = Math.Min(s - 1, y0 + 1);
            float ty = sy - y0;
            for (int x = 0; x < w; x++)
            {
                float sx = MathF.Max(0f, (x + 0.5f) * fx - 0.5f);
                int x0 = Math.Min(s - 1, (int)sx), x1 = Math.Min(s - 1, x0 + 1);
                float tx = sx - x0;
                float a = map[y0 * s + x0] + (map[y0 * s + x1] - map[y0 * s + x0]) * tx;
                float b = map[y1 * s + x0] + (map[y1 * s + x1] - map[y1 * s + x0]) * tx;
                mask[y * w + x] = (a + (b - a) * ty - mn) * inv;
            }
        });

        lock (CacheSync) cache = (w, h, hash, mask);
        return mask;
    }

    /// <summary>Area-averaged resize to s×s, scaled by the image maximum, ImageNet mean/std, CHW.</summary>
    private static float[] Prepare(uint[] px, int w, int h, int s)
    {
        var rgb = new float[3 * s * s];
        for (int ty = 0; ty < s; ty++)
        {
            double y0 = (double)ty * h / s, y1 = (double)(ty + 1) * h / s;
            for (int tx = 0; tx < s; tx++)
            {
                double x0 = (double)tx * w / s, x1 = (double)(tx + 1) * w / s;
                double r = 0, g = 0, b = 0, wsum = 0;
                for (int y = (int)y0; y < Math.Min(h, (int)Math.Ceiling(y1)); y++)
                {
                    double wy = Math.Min(y + 1, y1) - Math.Max(y, y0);
                    if (wy <= 0) continue;
                    for (int x = (int)x0; x < Math.Min(w, (int)Math.Ceiling(x1)); x++)
                    {
                        double wx = Math.Min(x + 1, x1) - Math.Max(x, x0);
                        if (wx <= 0) continue;
                        uint p = px[y * w + x];
                        double wt = wx * wy;
                        r += ((p >> 16) & 0xFF) * wt; g += ((p >> 8) & 0xFF) * wt; b += (p & 0xFF) * wt; wsum += wt;
                    }
                }
                int i = ty * s + tx;
                rgb[i] = (float)(r / wsum);
                rgb[s * s + i] = (float)(g / wsum);
                rgb[2 * s * s + i] = (float)(b / wsum);
            }
        }
        float max = 1e-6f;
        foreach (float v in rgb) max = MathF.Max(max, v);
        float[] mean = { 0.485f, 0.456f, 0.406f }, std = { 0.229f, 0.224f, 0.225f };
        for (int c = 0; c < 3; c++)
        {
            for (int i = 0; i < s * s; i++)
            {
                int k = c * s * s + i;
                rgb[k] = (rgb[k] / max - mean[c]) / std[c];
            }
        }
        return rgb;
    }

    private static ulong Hash(uint[] px, int w, int h)
    {
        ulong hash = 14695981039346656037UL ^ (ulong)w ^ ((ulong)h << 32);
        int step = Math.Max(1, px.Length / 1_000_000);
        for (int i = 0; i < w * h; i += step)
        {
            hash = (hash ^ px[i]) * 1099511628211UL;
        }
        return hash;
    }
}

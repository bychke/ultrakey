using System;
using System.Collections.Generic;
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

    private static readonly List<(int w, int h, ulong hash, int x0, int y0, int cw, int ch, float[] mask)> cache = new();

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
    public static float[]? Segment(uint[] px, int w, int h, CancellationToken ct) => Segment(px, w, h, 0, 0, w, h, ct);

    /// <summary>Segments only the crop (x0, y0, cw, ch); the result has the crop's size.</summary>
    public static float[]? Segment(uint[] px, int w, int h, int x0, int y0, int cw, int ch, CancellationToken ct)
    {
        ulong hash = Hash(px, w, h);
        lock (CacheSync)
        {
            foreach (var c in cache)
            {
                if (c.w == w && c.h == h && c.hash == hash && c.x0 == x0 && c.y0 == y0 && c.cw == cw && c.ch == ch) return c.mask;
            }
        }
        U2Net? net = Model.Value;
        if (net == null) return null;

        var crop = new uint[cw * ch];
        for (int y = 0; y < ch; y++) Array.Copy(px, (y0 + y) * w + x0, crop, y * cw, cw);

        int s = net.InputSize;
        float[] map = net.Run(Prepare(crop, cw, ch, s), ct);

        float mn = float.MaxValue, mx = float.MinValue;
        foreach (float v in map)
        {
            if (v < mn) mn = v;
            if (v > mx) mx = v;
        }
        float inv = mx > mn ? 1f / (mx - mn) : 0f;
        var mask = new float[cw * ch];
        float fx = (float)s / cw, fy = (float)s / ch;
        Parallel.For(0, ch, ImageOps.Options(ct), y =>
        {
            float sy = MathF.Max(0f, (y + 0.5f) * fy - 0.5f);
            int ya = Math.Min(s - 1, (int)sy), yb = Math.Min(s - 1, ya + 1);
            float ty = sy - ya;
            for (int x = 0; x < cw; x++)
            {
                float sx = MathF.Max(0f, (x + 0.5f) * fx - 0.5f);
                int xa = Math.Min(s - 1, (int)sx), xb = Math.Min(s - 1, xa + 1);
                float tx = sx - xa;
                float a = map[ya * s + xa] + (map[ya * s + xb] - map[ya * s + xa]) * tx;
                float b = map[yb * s + xa] + (map[yb * s + xb] - map[yb * s + xa]) * tx;
                mask[y * cw + x] = (a + (b - a) * ty - mn) * inv;
            }
        });

        lock (CacheSync)
        {
            cache.Add((w, h, hash, x0, y0, cw, ch, mask));
            if (cache.Count > 6) cache.RemoveAt(0);
        }
        return mask;
    }

    /// <summary>
    /// Finds the object under (x, y) that the full-image pass missed (e.g. a logo next to more salient objects):
    /// the network is re-run on progressively larger crops centred on the point until the object containing the
    /// point fits inside the crop. Returns a full-size mask (0 outside the object), or null if nothing is found.
    /// </summary>
    public static float[]? ObjectAt(uint[] px, int w, int h, int x, int y, CancellationToken ct)
    {
        float[]? best = null;
        foreach (double f in new[] { 0.35, 0.5, 0.7, 1.0 })
        {
            int cw = Math.Max(16, (int)(w * f)), ch = Math.Max(16, (int)(h * f));
            cw = Math.Min(cw, w);
            ch = Math.Min(ch, h);
            int x0 = Math.Clamp(x - cw / 2, 0, w - cw), y0 = Math.Clamp(y - ch / 2, 0, h - ch);
            float[]? m = Segment(px, w, h, x0, y0, cw, ch, ct);
            if (m == null) return null;
            // Bridge small gaps (letters of a word, parts of a logo) so they count as one object.
            var bin = new float[cw * ch];
            for (int i = 0; i < bin.Length; i++) bin[i] = m[i] >= 0.5f ? 1f : 0f;
            ImageOps.Morph(bin, cw, ch, Math.Max(2, Math.Min(cw, ch) / 30), ct);
            byte[]? comp = ImageOps.ComponentAt(bin, cw, ch, x - x0, y - y0, 0.5f, out _);
            if (comp == null) continue;

            // Does the object run into a crop edge that is not also an image edge? Then it was cut off.
            bool cut = false;
            for (int i = 0; i < cw && !cut; i++)
            {
                if (y0 > 0 && comp[i] != 0) cut = true;
                if (y0 + ch < h && comp[(ch - 1) * cw + i] != 0) cut = true;
            }
            for (int j = 0; j < ch && !cut; j++)
            {
                if (x0 > 0 && comp[j * cw] != 0) cut = true;
                if (x0 + cw < w && comp[j * cw + cw - 1] != 0) cut = true;
            }

            var full = new float[w * h];
            var grow = new float[cw * ch];
            for (int i = 0; i < grow.Length; i++) grow[i] = comp[i];
            ImageOps.Morph(grow, cw, ch, 3, ct); // keep the soft rim around the object
            for (int j = 0; j < ch; j++)
            {
                for (int i = 0; i < cw; i++)
                {
                    int k = j * cw + i;
                    if (grow[k] > 0f) full[(y0 + j) * w + x0 + i] = m[k];
                }
            }
            best = full;
            if (!cut) break;
        }
        return best;
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

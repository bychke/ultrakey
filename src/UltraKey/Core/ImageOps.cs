using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace UltraKey.Core;

/// <summary>Generic single-channel float image operations used by the keyer.</summary>
internal static class ImageOps
{
    public static ParallelOptions Options(CancellationToken ct) => new() { CancellationToken = ct };

    /// <summary>Mean filter with a (2r+1)^2 window, normalised by the number of pixels inside the image.</summary>
    public static void BoxBlur(float[] data, int w, int h, int r, CancellationToken ct)
    {
        if (r <= 0) return;
        var tmp = new float[data.Length];
        BoxBlurH(data, tmp, w, h, r, ct);
        BoxBlurV(tmp, data, w, h, r, ct);
    }

    private static void BoxBlurH(float[] src, float[] dst, int w, int h, int r, CancellationToken ct)
    {
        Parallel.For(0, h, Options(ct), y =>
        {
            int row = y * w;
            double sum = 0;
            int hi = Math.Min(r, w - 1);
            for (int x = 0; x <= hi; x++) sum += src[row + x];
            for (int x = 0; x < w; x++)
            {
                int lo = Math.Max(0, x - r);
                int top = Math.Min(w - 1, x + r);
                dst[row + x] = (float)(sum / (top - lo + 1));
                int add = x + r + 1;
                int rem = x - r;
                if (add < w) sum += src[row + add];
                if (rem >= 0) sum -= src[row + rem];
            }
        });
    }

    private static void BoxBlurV(float[] src, float[] dst, int w, int h, int r, CancellationToken ct)
    {
        // Process in column bands so each worker walks rows with good locality.
        const int band = 64;
        int bands = (w + band - 1) / band;
        Parallel.For(0, bands, Options(ct), bi =>
        {
            int x0 = bi * band;
            int x1 = Math.Min(w, x0 + band);
            int bw = x1 - x0;
            var sum = new double[bw];
            int hi = Math.Min(r, h - 1);
            for (int y = 0; y <= hi; y++)
            {
                int row = y * w;
                for (int x = 0; x < bw; x++) sum[x] += src[row + x0 + x];
            }
            for (int y = 0; y < h; y++)
            {
                int lo = Math.Max(0, y - r);
                int top = Math.Min(h - 1, y + r);
                double inv = 1.0 / (top - lo + 1);
                int row = y * w;
                for (int x = 0; x < bw; x++) dst[row + x0 + x] = (float)(sum[x] * inv);
                int add = y + r + 1;
                int rem = y - r;
                if (add < h)
                {
                    int ra = add * w + x0;
                    for (int x = 0; x < bw; x++) sum[x] += src[ra + x];
                }
                if (rem >= 0)
                {
                    int rr = rem * w + x0;
                    for (int x = 0; x < bw; x++) sum[x] -= src[rr + x];
                }
            }
        });
    }

    /// <summary>Gaussian blur approximated by three box blurs.</summary>
    public static void GaussianBlur(float[] data, int w, int h, double sigma, CancellationToken ct)
    {
        if (sigma < 0.2) return;
        foreach (int r in BoxesForGauss(sigma, 3))
        {
            BoxBlur(data, w, h, r, ct);
        }
    }

    private static IEnumerable<int> BoxesForGauss(double sigma, int n)
    {
        double wIdeal = Math.Sqrt(12 * sigma * sigma / n + 1);
        int wl = (int)Math.Floor(wIdeal);
        if (wl % 2 == 0) wl--;
        int wu = wl + 2;
        double mIdeal = (12 * sigma * sigma - n * wl * wl - 4 * n * wl - 3 * n) / (-4 * wl - 4);
        int m = (int)Math.Round(mIdeal);
        for (int i = 0; i < n; i++)
        {
            int size = i < m ? wl : wu;
            yield return Math.Max(0, (size - 1) / 2);
        }
    }

    /// <summary>
    /// Grey-scale morphology with an (approximately round) octagonal structuring element.
    /// radius &gt; 0 dilates (grow), radius &lt; 0 erodes (shrink). Fractional radii are interpolated.
    /// </summary>
    public static void Morph(float[] data, int w, int h, double radius, CancellationToken ct)
    {
        if (Math.Abs(radius) < 0.01) return;
        bool dilate = radius > 0;
        double rr = Math.Abs(radius);
        int steps = (int)Math.Floor(rr);
        float frac = (float)(rr - steps);
        var tmp = new float[data.Length];
        for (int i = 0; i < steps; i++)
        {
            MorphStep(data, tmp, w, h, dilate, square: (i & 1) == 1, ct);
        }
        if (frac > 0.01f)
        {
            var copy = (float[])data.Clone();
            MorphStep(copy, tmp, w, h, dilate, square: (steps & 1) == 1, ct);
            Parallel.For(0, h, Options(ct), y =>
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x;
                    data[i] += (copy[i] - data[i]) * frac;
                }
            });
        }
    }

    private static void MorphStep(float[] data, float[] tmp, int w, int h, bool dilate, bool square, CancellationToken ct)
    {
        if (square)
        {
            // Separable 3x3 square: horizontal pass into tmp, vertical pass back into data.
            Parallel.For(0, h, Options(ct), y =>
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    float v = data[row + x];
                    if (x > 0) v = dilate ? MathF.Max(v, data[row + x - 1]) : MathF.Min(v, data[row + x - 1]);
                    if (x < w - 1) v = dilate ? MathF.Max(v, data[row + x + 1]) : MathF.Min(v, data[row + x + 1]);
                    tmp[row + x] = v;
                }
            });
            Parallel.For(0, h, Options(ct), y =>
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    float v = tmp[row + x];
                    if (y > 0) v = dilate ? MathF.Max(v, tmp[row - w + x]) : MathF.Min(v, tmp[row - w + x]);
                    if (y < h - 1) v = dilate ? MathF.Max(v, tmp[row + w + x]) : MathF.Min(v, tmp[row + w + x]);
                    data[row + x] = v;
                }
            });
        }
        else
        {
            // 4-neighbour cross.
            Parallel.For(0, h, Options(ct), y =>
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x;
                    float v = data[i];
                    if (dilate)
                    {
                        if (x > 0) v = MathF.Max(v, data[i - 1]);
                        if (x < w - 1) v = MathF.Max(v, data[i + 1]);
                        if (y > 0) v = MathF.Max(v, data[i - w]);
                        if (y < h - 1) v = MathF.Max(v, data[i + w]);
                    }
                    else
                    {
                        if (x > 0) v = MathF.Min(v, data[i - 1]);
                        if (x < w - 1) v = MathF.Min(v, data[i + 1]);
                        if (y > 0) v = MathF.Min(v, data[i - w]);
                        if (y < h - 1) v = MathF.Min(v, data[i + w]);
                    }
                    tmp[i] = v;
                }
            });
            Array.Copy(tmp, data, data.Length);
        }
    }

    /// <summary>
    /// Push-pull (pyramid) scattered-data interpolation. Each channel holds values where
    /// <paramref name="weight"/> &gt; 0; on return every pixel holds a smooth interpolation of the known values.
    /// Returns false when there is no known pixel at all.
    /// </summary>
    public static bool PushPull(float[][] channels, float[] weight, int w, int h)
    {
        int nc = channels.Length;
        var levels = new List<(float[][] c, float[] wt, int w, int h)>();

        // Level 0: premultiply by weight, clamp weight to 1.
        var c0 = new float[nc][];
        var w0 = new float[w * h];
        for (int k = 0; k < nc; k++) c0[k] = new float[w * h];
        double total = 0;
        for (int i = 0; i < w * h; i++)
        {
            float wt = Math.Clamp(weight[i], 0f, 1f);
            w0[i] = wt;
            total += wt;
            for (int k = 0; k < nc; k++) c0[k][i] = channels[k][i] * wt;
        }
        if (total <= 0) return false;
        levels.Add((c0, w0, w, h));

        // Push: build pyramid down to 1x1.
        Span<float> acc = stackalloc float[nc];
        while (levels[^1].w > 1 || levels[^1].h > 1)
        {
            var (pc, pw, lw, lh) = levels[^1];
            int nw = (lw + 1) / 2, nh = (lh + 1) / 2;
            var nc2 = new float[nc][];
            for (int k = 0; k < nc; k++) nc2[k] = new float[nw * nh];
            var nwt = new float[nw * nh];
            for (int y = 0; y < nh; y++)
            {
                for (int x = 0; x < nw; x++)
                {
                    float ws = 0;
                    acc.Clear();
                    for (int dy = 0; dy < 2; dy++)
                    {
                        int sy = 2 * y + dy;
                        if (sy >= lh) continue;
                        for (int dx = 0; dx < 2; dx++)
                        {
                            int sx = 2 * x + dx;
                            if (sx >= lw) continue;
                            int si = sy * lw + sx;
                            ws += pw[si];
                            for (int k = 0; k < nc; k++) acc[k] += pc[k][si];
                        }
                    }
                    int di = y * nw + x;
                    if (ws > 1f)
                    {
                        float inv = 1f / ws;
                        for (int k = 0; k < nc; k++) nc2[k][di] = acc[k] * inv;
                        nwt[di] = 1f;
                    }
                    else
                    {
                        for (int k = 0; k < nc; k++) nc2[k][di] = acc[k];
                        nwt[di] = ws;
                    }
                }
            }
            levels.Add((nc2, nwt, nw, nh));
        }

        // Top level: normalise.
        {
            var (tc, tw, _, _) = levels[^1];
            float wt = tw[0];
            for (int k = 0; k < nc; k++) tc[k][0] = wt > 0 ? tc[k][0] / wt : 0;
            tw[0] = 1;
        }

        // Pull: fill each finer level from the (already complete) coarser level.
        for (int li = levels.Count - 2; li >= 0; li--)
        {
            var (fc, fw, lw, lh) = levels[li];
            var (cc, _, cw, ch) = levels[li + 1];
            for (int y = 0; y < lh; y++)
            {
                float cy = (y + 0.5f) * 0.5f - 0.5f;
                int y0 = (int)MathF.Floor(cy);
                float ty = cy - y0;
                int ya = Math.Clamp(y0, 0, ch - 1), yb = Math.Clamp(y0 + 1, 0, ch - 1);
                for (int x = 0; x < lw; x++)
                {
                    int i = y * lw + x;
                    float wt = fw[i];
                    if (wt >= 1f) continue;
                    float cx = (x + 0.5f) * 0.5f - 0.5f;
                    int x0 = (int)MathF.Floor(cx);
                    float tx = cx - x0;
                    int xa = Math.Clamp(x0, 0, cw - 1), xb = Math.Clamp(x0 + 1, 0, cw - 1);
                    float miss = 1f - wt;
                    for (int k = 0; k < nc; k++)
                    {
                        float[] c = cc[k];
                        float top = c[ya * cw + xa] + (c[ya * cw + xb] - c[ya * cw + xa]) * tx;
                        float bot = c[yb * cw + xa] + (c[yb * cw + xb] - c[yb * cw + xa]) * tx;
                        fc[k][i] += miss * (top + (bot - top) * ty);
                    }
                    fw[i] = 1f;
                }
            }
        }

        for (int k = 0; k < nc; k++) Array.Copy(levels[0].c[k], channels[k], w * h);
        return true;
    }

    /// <summary>
    /// Flood fill (4-connected) through passable pixels, starting from every passable border pixel
    /// (or only from the border pixels flagged in <paramref name="seeds"/>).
    /// </summary>
    public static byte[] FloodFromBorder(byte[] passable, int w, int h, byte[]? seeds = null)
    {
        var reached = new byte[w * h];
        var stack = new Stack<int>(1024);
        void Seed(int i)
        {
            if (passable[i] != 0 && reached[i] == 0)
            {
                reached[i] = 1;
                stack.Push(i);
            }
        }
        void Start(int i)
        {
            if (seeds == null || seeds[i] != 0) Seed(i);
        }
        for (int x = 0; x < w; x++)
        {
            Start(x);
            Start((h - 1) * w + x);
        }
        for (int y = 0; y < h; y++)
        {
            Start(y * w);
            Start(y * w + w - 1);
        }
        while (stack.Count > 0)
        {
            int i = stack.Pop();
            int x = i % w, y = i / w;
            if (x > 0) Seed(i - 1);
            if (x < w - 1) Seed(i + 1);
            if (y > 0) Seed(i - w);
            if (y < h - 1) Seed(i + w);
        }
        return reached;
    }

    /// <summary>
    /// Removes connected components (4-connected) smaller than <paramref name="minSize"/> pixels,
    /// both opaque islands in the background and transparent holes in the subject.
    /// </summary>
    public static void RemoveSmallComponents(float[] alpha, int w, int h, int minSize, CancellationToken ct)
    {
        if (minSize <= 1) return;
        int n = w * h;
        var visited = new byte[n];
        var stack = new int[n];
        var small = new int[minSize];
        for (int start = 0; start < n; start++)
        {
            if (visited[start] != 0) continue;
            if ((start & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
            bool fg = alpha[start] >= 0.5f;
            int sp = 0;
            int count = 0;
            stack[sp++] = start;
            visited[start] = 1;
            while (sp > 0)
            {
                int i = stack[--sp];
                if (count < minSize) small[count] = i;
                count++;
                int x = i % w, y = i / w;
                if (x > 0) Visit(i - 1);
                if (x < w - 1) Visit(i + 1);
                if (y > 0) Visit(i - w);
                if (y < h - 1) Visit(i + w);
            }
            if (count < minSize)
            {
                float v = fg ? 0f : 1f;
                for (int k = 0; k < count; k++) alpha[small[k]] = v;
            }

            void Visit(int j)
            {
                if (visited[j] == 0 && (alpha[j] >= 0.5f) == fg)
                {
                    visited[j] = 1;
                    stack[sp++] = j;
                }
            }
        }
    }

    /// <summary>
    /// Fills holes (4-connected zero regions of <paramref name="mask"/>) that are smaller than
    /// <paramref name="maxSize"/> pixels and whose mean <paramref name="values"/> is at least <paramref name="minMean"/>.
    /// Holes touching the image border are never filled.
    /// </summary>
    public static void FillHoles(float[] mask, float[] values, int w, int h, int maxSize, float minMean, CancellationToken ct)
    {
        int n = w * h;
        var visited = new byte[n];
        var stack = new int[n];
        var members = new int[maxSize];
        for (int start = 0; start < n; start++)
        {
            if (visited[start] != 0 || mask[start] >= 0.5f) continue;
            if ((start & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
            int sp = 0, count = 0;
            double sum = 0;
            bool border = false;
            stack[sp++] = start;
            visited[start] = 1;
            while (sp > 0)
            {
                int i = stack[--sp];
                if (count < maxSize) members[count] = i;
                count++;
                sum += values[i];
                int x = i % w, y = i / w;
                if (x == 0 || y == 0 || x == w - 1 || y == h - 1) border = true;
                if (x > 0) Visit(i - 1);
                if (x < w - 1) Visit(i + 1);
                if (y > 0) Visit(i - w);
                if (y < h - 1) Visit(i + w);
            }
            if (!border && count < maxSize && sum / count >= minMean)
            {
                for (int k = 0; k < count; k++) mask[members[k]] = 1f;
            }

            void Visit(int j)
            {
                if (visited[j] == 0 && mask[j] < 0.5f)
                {
                    visited[j] = 1;
                    stack[sp++] = j;
                }
            }
        }
    }

    /// <summary>Bilinear sample of a low-resolution grid that covers the full-resolution image with block size <paramref name="factor"/>.</summary>
    public static float SampleBilinear(float[] grid, int gw, int gh, float factor, int x, int y)
    {
        float gx = (x + 0.5f) / factor - 0.5f;
        float gy = (y + 0.5f) / factor - 0.5f;
        int x0 = (int)MathF.Floor(gx), y0 = (int)MathF.Floor(gy);
        float tx = gx - x0, ty = gy - y0;
        int xa = Math.Clamp(x0, 0, gw - 1), xb = Math.Clamp(x0 + 1, 0, gw - 1);
        int ya = Math.Clamp(y0, 0, gh - 1), yb = Math.Clamp(y0 + 1, 0, gh - 1);
        float top = grid[ya * gw + xa] + (grid[ya * gw + xb] - grid[ya * gw + xa]) * tx;
        float bot = grid[yb * gw + xa] + (grid[yb * gw + xb] - grid[yb * gw + xa]) * tx;
        return top + (bot - top) * ty;
    }
}

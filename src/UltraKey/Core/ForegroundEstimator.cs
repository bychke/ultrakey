using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace UltraKey.Core;

internal interface IImageSource
{
    void Get(int x, int y, out float r, out float g, out float b);
}

/// <summary>Linear RGB read straight from packed sRGB pixels.</summary>
internal readonly struct PixelSource : IImageSource
{
    private readonly uint[] px;
    private readonly int w;

    public PixelSource(uint[] px, int w)
    {
        this.px = px;
        this.w = w;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Get(int x, int y, out float r, out float g, out float b) => ColorMath.PixelToLinear(px[y * w + x], out r, out g, out b);
}

internal readonly struct ArraySource : IImageSource
{
    private readonly float[] r, g, b;
    private readonly int w;

    public ArraySource(float[] r, float[] g, float[] b, int w)
    {
        this.r = r;
        this.g = g;
        this.b = b;
        this.w = w;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Get(int x, int y, out float vr, out float vg, out float vb)
    {
        int i = y * w + x;
        vr = r[i];
        vg = g[i];
        vb = b[i];
    }
}

/// <summary>
/// Multi-level foreground / background colour estimation (Germer, Uelwer, Conrad, Harmeling —
/// "Fast Multi-Level Foreground Estimation", ICPR 2020). Given the image and its alpha matte, it solves
/// I = a*F + (1-a)*B for smooth F and B. F is the "decontaminated" subject colour: semi-transparent edge
/// pixels lose the background colour that bled into them, which is what makes hair look natural on a new background.
/// </summary>
internal static class ForegroundEstimator
{
    private const float Regularization = 1e-5f;
    private const float GradientWeight = 1f;
    private const int SmallSize = 32;
    private const int SmallIterations = 10;
    private const int BigIterations = 2;

    public sealed class Planes
    {
        public readonly float[] R, G, B;

        public Planes(int n)
        {
            R = new float[n];
            G = new float[n];
            B = new float[n];
        }
    }

    /// <param name="active">
    /// Optional mask for the full-resolution level: only flagged pixels are iterated there. Elsewhere the alpha is
    /// 0 or 1, so F (or B) simply equals the image and the other plane keeps the coarse-level estimate.
    /// </param>
    public static void Estimate<TSrc>(TSrc src, float[] alpha0, int w0, int h0, Planes fOut, Planes bOut, CancellationToken ct, byte[]? active = null)
        where TSrc : struct, IImageSource
    {
        int nLevels = Math.Max(1, (int)Math.Ceiling(Math.Log2(Math.Max(w0, h0))));

        // Start from the mean colour.
        double sr = 0, sg = 0, sb = 0;
        int cnt = 0;
        int step = Math.Max(1, (int)Math.Sqrt((double)w0 * h0 / 4096));
        for (int y = 0; y < h0; y += step)
        {
            for (int x = 0; x < w0; x += step)
            {
                src.Get(x, y, out float r, out float g, out float b);
                sr += r; sg += g; sb += b; cnt++;
            }
        }
        var prevF = new Planes(1);
        var prevB = new Planes(1);
        prevF.R[0] = prevB.R[0] = (float)(sr / cnt);
        prevF.G[0] = prevB.G[0] = (float)(sg / cnt);
        prevF.B[0] = prevB.B[0] = (float)(sb / cnt);
        int pw = 1, ph = 1;

        for (int level = 0; level <= nLevels; level++)
        {
            ct.ThrowIfCancellationRequested();
            int w = Math.Max(1, (int)Math.Round(Math.Pow(w0, (double)level / nLevels)));
            int h = Math.Max(1, (int)Math.Round(Math.Pow(h0, (double)level / nLevels)));
            bool full = level == nLevels;
            if (full)
            {
                w = w0;
                h = h0;
            }

            Planes f = full ? fOut : new Planes(w * h);
            Planes b = full ? bOut : new Planes(w * h);
            ResizeNearest(prevF, pw, ph, f, w, h, ct);
            ResizeNearest(prevB, pw, ph, b, w, h, ct);

            int iterations = (w <= SmallSize && h <= SmallSize) ? SmallIterations : BigIterations;
            if (full)
            {
                if (active != null) PinInactive(src, alpha0, w, h, f, b, active, ct);
                Iterate(src, alpha0, w, h, f, b, iterations, ct, active);
            }
            else
            {
                var img = new Planes(w * h);
                var alpha = new float[w * h];
                int lw = w, lh = h;
                Parallel.For(0, lh, ImageOps.Options(ct), y =>
                {
                    int sy = Math.Min(h0 - 1, (int)((long)y * h0 / lh));
                    for (int x = 0; x < lw; x++)
                    {
                        int sx = Math.Min(w0 - 1, (int)((long)x * w0 / lw));
                        int i = y * lw + x;
                        src.Get(sx, sy, out img.R[i], out img.G[i], out img.B[i]);
                        alpha[i] = alpha0[sy * w0 + sx];
                    }
                });
                Iterate(new ArraySource(img.R, img.G, img.B, w), alpha, w, h, f, b, iterations, ct);
            }

            prevF = f;
            prevB = b;
            pw = w;
            ph = h;
        }
    }

    private static void ResizeNearest(Planes src, int sw, int sh, Planes dst, int dw, int dh, CancellationToken ct)
    {
        Parallel.For(0, dh, ImageOps.Options(ct), y =>
        {
            int sy = Math.Min(sh - 1, (int)((long)y * sh / dh));
            for (int x = 0; x < dw; x++)
            {
                int sx = Math.Min(sw - 1, (int)((long)x * sw / dw));
                int si = sy * sw + sx, di = y * dw + x;
                dst.R[di] = src.R[si];
                dst.G[di] = src.G[si];
                dst.B[di] = src.B[si];
            }
        });
    }

    private static void PinInactive<TImg>(TImg img, float[] alpha, int w, int h, Planes f, Planes b, byte[] active, CancellationToken ct)
        where TImg : struct, IImageSource
    {
        Parallel.For(0, h, ImageOps.Options(ct), y =>
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int i = row + x;
                if (active[i] != 0) continue;
                img.Get(x, y, out float r, out float g, out float bl);
                if (alpha[i] >= 0.5f)
                {
                    f.R[i] = r; f.G[i] = g; f.B[i] = bl;
                }
                else
                {
                    b.R[i] = r; b.G[i] = g; b.B[i] = bl;
                }
            }
        });
    }

    private static void Iterate<TImg>(TImg img, float[] alpha, int w, int h, Planes f, Planes b, int iterations, CancellationToken ct, byte[]? active = null)
        where TImg : struct, IImageSource
    {
        bool[]? rowActive = null;
        if (active != null)
        {
            rowActive = new bool[h];
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    if (active[row + x] != 0)
                    {
                        rowActive[y] = true;
                        break;
                    }
                }
            }
        }
        for (int it = 0; it < iterations; it++)
        {
            // Red-black Gauss-Seidel: pixels of one parity only read neighbours of the other parity.
            for (int parity = 0; parity < 2; parity++)
            {
                int par = parity;
                if (h * w < 4096)
                {
                    for (int y = 0; y < h; y++) Row(img, alpha, w, h, f, b, y, par, active);
                }
                else
                {
                    Parallel.For(0, h, ImageOps.Options(ct), y =>
                    {
                        if (rowActive == null || rowActive[y]) Row(img, alpha, w, h, f, b, y, par, active);
                    });
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Row<TImg>(TImg img, float[] alpha, int w, int h, Planes f, Planes b, int y, int parity, byte[]? active)
        where TImg : struct, IImageSource
    {
        float[] fr = f.R, fg = f.G, fb = f.B, br = b.R, bg = b.G, bb = b.B;
        int row = y * w;
        int up = y > 0 ? -w : 0;
        int down = y < h - 1 ? w : 0;
        for (int x = (y + parity) & 1; x < w; x += 2)
        {
            int i = row + x;
            if (active != null && active[i] == 0) continue;
            float a0 = alpha[i];
            float a1 = 1f - a0;
            float a00 = a0 * a0, a01 = a0 * a1, a11 = a1 * a1;
            img.Get(x, y, out float cr, out float cg, out float cb);
            float b0r = a0 * cr, b0g = a0 * cg, b0b = a0 * cb;
            float b1r = a1 * cr, b1g = a1 * cg, b1b = a1 * cb;

            int left = x > 0 ? -1 : 0;
            int right = x < w - 1 ? 1 : 0;

            Accumulate(i + left);
            Accumulate(i + right);
            Accumulate(i + up);
            Accumulate(i + down);

            float det = a00 * a11 - a01 * a01;
            float inv = 1f / det;
            float m00 = inv * a11, m01 = -inv * a01, m11 = inv * a00;
            fr[i] = ColorMath.Clamp01(m00 * b0r + m01 * b1r);
            fg[i] = ColorMath.Clamp01(m00 * b0g + m01 * b1g);
            fb[i] = ColorMath.Clamp01(m00 * b0b + m01 * b1b);
            br[i] = ColorMath.Clamp01(m01 * b0r + m11 * b1r);
            bg[i] = ColorMath.Clamp01(m01 * b0g + m11 * b1g);
            bb[i] = ColorMath.Clamp01(m01 * b0b + m11 * b1b);

            void Accumulate(int j)
            {
                float da = Regularization + GradientWeight * MathF.Abs(a0 - alpha[j]);
                a00 += da;
                a11 += da;
                b0r += da * fr[j]; b0g += da * fg[j]; b0b += da * fb[j];
                b1r += da * br[j]; b1g += da * bg[j]; b1b += da * bb[j];
            }
        }
    }
}

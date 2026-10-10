using System;
using System.Threading;
using System.Threading.Tasks;

namespace UltraKey.Core;

/// <summary>
/// Colour guided filter (He, Sun, Tang — "Guided Image Filtering", TPAMI 2013) used to snap the matte
/// to the real edges of the image (hair, fur, fine detail). Works tile-by-tile so memory stays small,
/// and only processes tiles that contain pixels flagged in the mask.
/// </summary>
internal static class GuidedFilter
{
    private const int TileSize = 128;

    public static void Apply(uint[] px, float[] p, byte[] mask, int w, int h, int r, float eps, CancellationToken ct)
    {
        if (r < 1) return;
        int tilesX = (w + TileSize - 1) / TileSize;
        int tilesY = (h + TileSize - 1) / TileSize;
        var output = (float[])p.Clone();
        int ext = TileSize + 4 * r;
        Parallel.For(0, tilesX * tilesY, ImageOps.Options(ct), () => new Buffers(ext * ext, ext), (t, _, buf) =>
        {
            int tx = t % tilesX, ty = t / tilesX;
            int x0 = tx * TileSize, y0 = ty * TileSize;
            int x1 = Math.Min(w, x0 + TileSize), y1 = Math.Min(h, y0 + TileSize);
            if (AnyMasked(mask, w, x0, y0, x1, y1))
            {
                ProcessTile(px, p, output, mask, w, h, x0, y0, x1, y1, r, eps, buf);
            }
            return buf;
        }, _ => { });
        Array.Copy(output, p, p.Length);
    }

    private static bool AnyMasked(byte[] mask, int w, int x0, int y0, int x1, int y1)
    {
        for (int y = y0; y < y1; y++)
        {
            int row = y * w;
            for (int x = x0; x < x1; x++)
            {
                if (mask[row + x] != 0) return true;
            }
        }
        return false;
    }

    private sealed class Buffers
    {
        public readonly float[][] Planes;
        public readonly double[] ColSum;

        public Buffers(int n, int width)
        {
            Planes = new float[17][];
            for (int i = 0; i < Planes.Length; i++) Planes[i] = new float[n];
            ColSum = new double[width];
        }
    }

    private static void ProcessTile(uint[] px, float[] p, float[] output, byte[] mask, int w, int h, int x0, int y0, int x1, int y1, int r, float eps, Buffers buf)
    {
        // Extended tile: q at the core needs mean(a,b) over +-r, which needs the statistics over +-2r.
        int ex0 = Math.Max(0, x0 - 2 * r), ey0 = Math.Max(0, y0 - 2 * r);
        int ex1 = Math.Min(w, x1 + 2 * r), ey1 = Math.Min(h, y1 + 2 * r);
        int tw = ex1 - ex0, th = ey1 - ey0, n = tw * th;

        float[][] pl = buf.Planes;
        float[] ir = pl[0], ig = pl[1], ib = pl[2], mp = pl[3];
        float[] mr = pl[4], mg = pl[5], mb = pl[6];
        float[] rr = pl[7], rg = pl[8], rb = pl[9];
        float[] gg = pl[10], gb = pl[11], bb = pl[12];
        float[] pr = pl[13], pg = pl[14], pb = pl[15];
        float[] tmp = pl[16];
        double[] colSum = buf.ColSum;

        const float inv255 = 1f / 255f;
        for (int y = 0; y < th; y++)
        {
            int srow = (y + ey0) * w + ex0;
            int drow = y * tw;
            for (int x = 0; x < tw; x++)
            {
                uint c = px[srow + x];
                float vr = ((c >> 16) & 0xFF) * inv255, vg = ((c >> 8) & 0xFF) * inv255, vb = (c & 0xFF) * inv255;
                float vp = p[srow + x];
                int i = drow + x;
                ir[i] = vr; ig[i] = vg; ib[i] = vb;
                mr[i] = vr; mg[i] = vg; mb[i] = vb;
                mp[i] = vp;
                rr[i] = vr * vr; rg[i] = vr * vg; rb[i] = vr * vb;
                gg[i] = vg * vg; gb[i] = vg * vb; bb[i] = vb * vb;
                pr[i] = vp * vr; pg[i] = vp * vg; pb[i] = vp * vb;
            }
        }

        Box(mr, tw, th, r, tmp, colSum); Box(mg, tw, th, r, tmp, colSum); Box(mb, tw, th, r, tmp, colSum);
        Box(mp, tw, th, r, tmp, colSum);
        Box(rr, tw, th, r, tmp, colSum); Box(rg, tw, th, r, tmp, colSum); Box(rb, tw, th, r, tmp, colSum);
        Box(gg, tw, th, r, tmp, colSum); Box(gb, tw, th, r, tmp, colSum); Box(bb, tw, th, r, tmp, colSum);
        Box(pr, tw, th, r, tmp, colSum); Box(pg, tw, th, r, tmp, colSum); Box(pb, tw, th, r, tmp, colSum);

        // Solve the per-window linear model. Results reuse the statistic buffers:
        // ar, ag, ab -> rr, gg, bb ; b -> mp.
        for (int i = 0; i < n; i++)
        {
            float mR = mr[i], mG = mg[i], mB = mb[i], mP = mp[i];
            float s11 = rr[i] - mR * mR + eps, s12 = rg[i] - mR * mG, s13 = rb[i] - mR * mB;
            float s22 = gg[i] - mG * mG + eps, s23 = gb[i] - mG * mB;
            float s33 = bb[i] - mB * mB + eps;
            float cr = pr[i] - mP * mR, cg = pg[i] - mP * mG, cb = pb[i] - mP * mB;

            float i11 = s22 * s33 - s23 * s23;
            float i12 = s13 * s23 - s12 * s33;
            float i13 = s12 * s23 - s13 * s22;
            float i22 = s11 * s33 - s13 * s13;
            float i23 = s13 * s12 - s11 * s23;
            float i33 = s11 * s22 - s12 * s12;
            float det = s11 * i11 + s12 * i12 + s13 * i13;
            float inv = det != 0f ? 1f / det : 0f;

            float ar = (i11 * cr + i12 * cg + i13 * cb) * inv;
            float ag = (i12 * cr + i22 * cg + i23 * cb) * inv;
            float ab = (i13 * cr + i23 * cg + i33 * cb) * inv;
            rr[i] = ar; gg[i] = ag; bb[i] = ab;
            mp[i] = mP - ar * mR - ag * mG - ab * mB;
        }

        Box(rr, tw, th, r, tmp, colSum);
        Box(gg, tw, th, r, tmp, colSum);
        Box(bb, tw, th, r, tmp, colSum);
        Box(mp, tw, th, r, tmp, colSum);

        for (int y = y0; y < y1; y++)
        {
            int ly = y - ey0;
            for (int x = x0; x < x1; x++)
            {
                int gi = y * w + x;
                if (mask[gi] == 0) continue;
                int li = ly * tw + (x - ex0);
                float q = rr[li] * ir[li] + gg[li] * ig[li] + bb[li] * ib[li] + mp[li];
                output[gi] = ColorMath.Clamp01(q);
            }
        }
    }

    /// <summary>Normalised box filter on a small buffer (single threaded).</summary>
    private static void Box(float[] a, int w, int h, int r, float[] tmp, double[] colSum)
    {
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            double sum = 0;
            int hi = Math.Min(r, w - 1);
            for (int x = 0; x <= hi; x++) sum += a[row + x];
            for (int x = 0; x < w; x++)
            {
                int lo = Math.Max(0, x - r), top = Math.Min(w - 1, x + r);
                tmp[row + x] = (float)(sum / (top - lo + 1));
                if (x + r + 1 < w) sum += a[row + x + r + 1];
                if (x - r >= 0) sum -= a[row + x - r];
            }
        }

        Array.Clear(colSum, 0, w);
        int vhi = Math.Min(r, h - 1);
        for (int y = 0; y <= vhi; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++) colSum[x] += tmp[row + x];
        }
        for (int y = 0; y < h; y++)
        {
            int lo = Math.Max(0, y - r), top = Math.Min(h - 1, y + r);
            double inv = 1.0 / (top - lo + 1);
            int row = y * w;
            for (int x = 0; x < w; x++) a[row + x] = (float)(colSum[x] * inv);
            if (y + r + 1 < h)
            {
                int ra = (y + r + 1) * w;
                for (int x = 0; x < w; x++) colSum[x] += tmp[ra + x];
            }
            if (y - r >= 0)
            {
                int rm = (y - r) * w;
                for (int x = 0; x < w; x++) colSum[x] -= tmp[rm + x];
            }
        }
    }
}

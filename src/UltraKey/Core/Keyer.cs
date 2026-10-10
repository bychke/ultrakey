using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace UltraKey.Core;

/// <summary>
/// The UltraKey background remover / chroma keyer. Pure managed code with no Paint.NET dependency.
/// <para>Pipeline:</para>
/// <list type="number">
/// <item>Background model: k-means of the image border (Auto) or a detected / chosen key colour.</item>
/// <item>Perceptual (Oklab) distance with adjustable luminance weight.</item>
/// <item>Clean-plate estimation (push-pull interpolation of the background) to handle uneven lighting and gradients.</item>
/// <item>Soft matte, enclosed-area protection, speck removal.</item>
/// <item>Edge detail: colour-line projection alpha + colour guided filter.</item>
/// <item>Matte controls (clip, shrink/grow, feather).</item>
/// <item>Multi-level foreground estimation (colour decontamination), despill, shadow keeping, output.</item>
/// </list>
/// </summary>
public static class Keyer
{
    /// <summary>Processes a BGRA image. Returns null when cancelled.</summary>
    public static uint[]? Process(uint[] pixels, int width, int height, KeyerSettings settings, CancellationToken cancellationToken = default)
    {
        if (width <= 0 || height <= 0 || pixels.Length < width * height) throw new ArgumentException("Bad image size.");
        try
        {
            return new KeyerRun(pixels, width, height, settings, cancellationToken).Run();
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }
}

internal sealed class KeyerRun
{
    /// <summary>Test hook: receives intermediate planes (name, data, width, height).</summary>
    internal static Action<string, float[], int, int>? Debug { get; set; }

    /// <summary>Test hook: receives stage timings.</summary>
    internal static Action<string>? Trace { get; set; }

    private readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();

    private void Mark(string stage)
    {
        Trace?.Invoke($"{stage}: {clock.ElapsedMilliseconds} ms");
        clock.Restart();
    }

    private static readonly int DefaultGreen = 0x00B140;
    private static readonly int DefaultBlue = 0x0047BB;

    private readonly uint[] px;
    private readonly int w, h, n;
    private KeyerSettings s;
    private readonly CancellationToken ct;
    private readonly ParallelOptions po;

    private float[]? labL, labA, labB;
    private Lab[] model = Array.Empty<Lab>();
    private float lumaW, tol, soft;

    // Clean plate (low resolution, Oklab).
    private float[]? plateL, plateA, plateB;
    private float[]? plateR, plateG, plateBl; // the same plate in linear RGB
    private int pw, ph;
    private float pf;

    private float[] alpha = Array.Empty<float>();
    private float[]? protectMask;

    // Effective mode: Auto switches to chroma keying when it finds a green / blue screen.
    private bool chromaMode;
    private float edgeScale = 1f; // > 1 when the matte comes from a lower resolution (AI mask)
    private bool keepEnclosed;

    public KeyerRun(uint[] px, int w, int h, KeyerSettings s, CancellationToken ct)
    {
        this.px = px;
        this.w = w;
        this.h = h;
        n = w * h;
        this.s = s;
        this.ct = ct;
        po = ImageOps.Options(ct);
    }

    public uint[] Run()
    {
        if (s.Mode == KeyMode.AiSubject)
        {
            float[]? mask = Ai.AiSegmenter.Segment(px, w, h, ct);
            Mark("ai");
            if (mask != null) return RunAi(mask);
            s = s with { Mode = KeyMode.Auto }; // model not shipped: fall back to background detection
        }

        ComputeLab();
        Mark("lab");
        BuildModel();
        ConfigureThresholds();
        Mark("model");

        var dist = new float[n];
        ComputeGlobalDistance(dist);
        Debug?.Invoke("dglobal", dist, w, h);
        Mark("distance");
        if (s.UnevenCorrection)
        {
            BuildCleanPlate(dist);
            Debug?.Invoke("dmin", dist, w, h);
            Mark("plate");
        }

        if (!chromaMode) AdaptToleranceToNoise(dist);

        alpha = new float[n];
        if (!(chromaMode && ColorDifferenceAlpha(dist)))
        {
            float lo = tol, hi = tol + soft;
            Parallel.For(0, h, po, y =>
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    alpha[row + x] = ColorMath.SmoothStep(lo, hi, dist[row + x]);
                }
            });
        }
        Debug?.Invoke("alpha0", alpha, w, h);
        Mark("matte");

        if (keepEnclosed)
        {
            ProtectEnclosed();
        }
        ImageOps.RemoveSmallComponents(alpha, w, h, s.SpeckSize, ct);

        // Lab is no longer needed; free it before the memory hungry stages.
        labL = labA = labB = null;
        Debug?.Invoke("alpha1", alpha, w, h);
        Mark("cleanup");

        if (s.EdgeDetail > 0)
        {
            ProjectEdges(dist);
            Debug?.Invoke("alpha_proj", alpha, w, h);
            Mark("project");
            RefineWithGuidedFilter();
            Debug?.Invoke("alpha_gf", alpha, w, h);
            Mark("guided");
        }

        // Edge refinement must not reopen what the enclosed-area protection closed.
        if (protectMask != null)
        {
            for (int i = 0; i < n; i++)
            {
                if (protectMask[i] > alpha[i]) alpha[i] = protectMask[i];
            }
        }
        if (s.SolidCore) FillSolidCore();
        Debug?.Invoke("alpha_core", alpha, w, h);
        Mark("core");

        AdjustMatte();
        Mark("adjust");
        var output = Compose();
        Mark("compose");
        return output;
    }

    /// <summary>
    /// AI mode: the network's subject mask replaces the colour model. It is only 320×320, so the outline is
    /// rebuilt on the real pixels with the same edge machinery (colour-line projection, guided filter) and the
    /// colours are decontaminated as usual.
    /// </summary>
    private uint[] RunAi(float[] mask)
    {
        chromaMode = false;
        keepEnclosed = false;
        model = Array.Empty<Lab>();
        ConfigureThresholds();
        edgeScale = MathF.Max(1f, Math.Max(w, h) / 320f);

        // Tolerance shifts the decision towards removing more, softness widens the transition.
        float t0 = (float)(0.5 * Math.Clamp(s.Tolerance, 0, 100) / 100.0);
        float t1 = MathF.Min(1f, t0 + (float)(0.2 + 0.6 * Math.Clamp(s.Softness, 0, 100) / 100.0));
        alpha = new float[n];
        Parallel.For(0, h, po, y =>
        {
            int row = y * w;
            for (int x = 0; x < w; x++) alpha[row + x] = ColorMath.SmoothStep(t0, t1, mask[row + x]);
        });
        ImageOps.RemoveSmallComponents(alpha, w, h, s.SpeckSize, ct);
        ApplyAiHints();
        Debug?.Invoke("alpha0", alpha, w, h);

        if (s.EdgeDetail > 0)
        {
            ProjectEdges(null);
            Debug?.Invoke("alpha_proj", alpha, w, h);
            RefineWithGuidedFilter();
            Debug?.Invoke("alpha_gf", alpha, w, h);
        }
        if (s.SolidCore) FillSolidCore();
        AdjustMatte();
        var output = Compose();
        Mark("compose");
        return output;
    }

    private (int x, int y) PointToPixel(double px1, double py1) =>
        (Math.Clamp((int)Math.Round((px1 + 1) * 0.5 * (w - 1)), 0, w - 1),
         Math.Clamp((int)Math.Round((py1 + 1) * 0.5 * (h - 1)), 0, h - 1));

    /// <summary>User hints for AI mode: add the object under the "keep" point, drop the one under the "remove" point.</summary>
    private void ApplyAiHints()
    {
        if (s.KeepPointOn)
        {
            var (kx, ky) = PointToPixel(s.KeepX, s.KeepY);
            float[]? obj = Ai.AiSegmenter.ObjectAt(px, w, h, kx, ky, ct);
            if (obj != null)
            {
                for (int i = 0; i < n; i++)
                {
                    if (obj[i] > alpha[i]) alpha[i] = obj[i];
                }
            }
        }
        if (s.RemovePointOn)
        {
            var (rx, ry) = PointToPixel(s.RemoveX, s.RemoveY);
            byte[]? comp = ImageOps.ComponentAt(alpha, w, h, rx, ry, 0.5f, out _);
            if (comp != null)
            {
                var grow = new float[n];
                for (int i = 0; i < n; i++) grow[i] = comp[i];
                ImageOps.Morph(grow, w, h, 3, ct);
                for (int i = 0; i < n; i++)
                {
                    if (grow[i] > 0f) alpha[i] = 0f;
                }
            }
        }
    }

    // ------------------------------------------------------------------ colour space

    private void ComputeLab()
    {
        labL = new float[n];
        labA = new float[n];
        labB = new float[n];
        float[] L = labL, A = labA, B = labB;
        Parallel.For(0, h, po, y =>
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int i = row + x;
                ColorMath.PixelToLinear(px[i], out float r, out float g, out float b);
                ColorMath.LinearToOklab(r, g, b, out L[i], out A[i], out B[i]);
            }
        });
    }

    private Lab LabAt(int i) => new(labL![i], labA![i], labB![i]);

    private bool Opaque(int i) => (px[i] >> 24) >= 128;

    // ------------------------------------------------------------------ background model

    private void BuildModel()
    {
        chromaMode = s.IsChromaMode;
        keepEnclosed = s.KeepEnclosed;
        model = s.Mode switch
        {
            KeyMode.Auto => SampleBorderModel(),
            KeyMode.GreenScreen => new[] { DetectScreen(green: true) },
            KeyMode.BlueScreen => new[] { DetectScreen(green: false) },
            KeyMode.CustomColor => new[] { ColorMath.RgbToLab(s.CustomKeyRgb) },
            KeyMode.PrimaryColor => new[] { ColorMath.RgbToLab(s.PrimaryRgb) },
            KeyMode.PickPoint => new[] { SamplePoint() },
            _ => new[] { ColorMath.RgbToLab(DefaultGreen) },
        };

        if (s.Mode == KeyMode.Auto && IsChromaScreen(model, out Lab key))
        {
            chromaMode = true;
            model = new[] { key };
            if (s.Enclosed == EnclosedAreas.Smart) keepEnclosed = false;
        }
    }

    /// <summary>
    /// True when every background cluster is a saturated green / blue / cyan of a similar hue — i.e. a chroma
    /// screen. The key is the most saturated cluster (the cleanest part of the screen).
    /// </summary>
    private static bool IsChromaScreen(Lab[] clusters, out Lab key)
    {
        key = default;
        if (clusters.Length == 0) return false;
        float hue0 = MathF.Atan2(clusters[0].B, clusters[0].A);
        float best = -1f;
        foreach (var c in clusters)
        {
            if (c.Chroma < 0.07f) return false;
            float dh = MathF.Abs(MathF.Atan2(c.B, c.A) - hue0);
            if (dh > MathF.PI) dh = 2f * MathF.PI - dh;
            if (dh > 0.6f) return false;
            c.ToLinear(out float r, out float g, out float b);
            if (!KeyChannels(r, g, b, out bool dr, out _, out _, out _) || dr) return false;
            if (c.Chroma > best)
            {
                best = c.Chroma;
                key = c;
            }
        }
        return true;
    }

    private void ConfigureThresholds()
    {
        double t = Math.Clamp(s.Tolerance, 0, 100) / 100.0;
        double so = Math.Clamp(s.Softness, 0, 100) / 100.0;
        double ls = Math.Clamp(s.LumaSensitivity, 0, 100) / 100.0;
        // Plain backdrops (Auto) are usually uniform or handled by the clean plate, and subjects such as skin can be
        // close to grey in Oklab, so the same slider positions map to tighter thresholds there.
        float scale = chromaMode ? 1f : 0.5f;
        tol = scale * (float)(0.004 + 0.40 * Math.Pow(t, 1.3));
        soft = scale * (float)(0.004 + 0.40 * Math.Pow(so, 1.3));
        // Chroma keys should ignore brightness (shadows / creases on the screen); plain
        // backgrounds (white, grey, black studio) need brightness to separate the subject.
        lumaW = chromaMode ? (float)(0.05 + 1.0 * ls) : (float)(0.25 + 1.6 * ls);
    }

    private Lab SamplePoint()
    {
        int cx = Math.Clamp((int)Math.Round((s.PickX + 1) * 0.5 * (w - 1)), 0, w - 1);
        int cy = Math.Clamp((int)Math.Round((s.PickY + 1) * 0.5 * (h - 1)), 0, h - 1);
        var ls = new List<float>(); var la = new List<float>(); var lb = new List<float>();
        for (int y = Math.Max(0, cy - 3); y <= Math.Min(h - 1, cy + 3); y++)
        {
            for (int x = Math.Max(0, cx - 3); x <= Math.Min(w - 1, cx + 3); x++)
            {
                int i = y * w + x;
                ls.Add(labL![i]); la.Add(labA![i]); lb.Add(labB![i]);
            }
        }
        return new Lab(Median(ls), Median(la), Median(lb));
    }

    private static float MedianOf(float[] v, int count)
    {
        Array.Sort(v, 0, count);
        return v[count / 2];
    }

    private static float Median(List<float> v)
    {
        if (v.Count == 0) return 0;
        v.Sort();
        return v[v.Count / 2];
    }

    /// <summary>Finds the actual shade of a green / blue screen: median of the most saturated screen-like pixels.</summary>
    private Lab DetectScreen(bool green)
    {
        int step = Math.Max(1, (int)Math.Sqrt(n / 250000.0));
        var cand = new List<(float c, Lab lab)>();
        for (int y = 0; y < h; y += step)
        {
            for (int x = 0; x < w; x += step)
            {
                int i = y * w + x;
                ColorMath.Unpack(px[i], out int r, out int g, out int b, out int a);
                if (a < 128) continue;
                bool ok = green
                    ? g > 40 && g > r + 20 && g > b + 20
                    : b > 40 && b > r + 20 && b > g + 10;
                if (!ok) continue;
                var lab = LabAt(i);
                cand.Add((lab.Chroma, lab));
            }
        }
        int samples = ((h + step - 1) / step) * ((w + step - 1) / step);
        if (cand.Count < Math.Max(50, samples / 200))
        {
            return ColorMath.RgbToLab(green ? DefaultGreen : DefaultBlue);
        }
        cand.Sort((p, q) => q.c.CompareTo(p.c));
        int take = Math.Max(1, cand.Count / 2);
        var ls = new List<float>(take); var la = new List<float>(take); var lb = new List<float>(take);
        for (int k = 0; k < take; k++)
        {
            ls.Add(cand[k].lab.L); la.Add(cand[k].lab.A); lb.Add(cand[k].lab.B);
        }
        return new Lab(Median(ls), Median(la), Median(lb));
    }

    /// <summary>
    /// Auto mode: clusters the colours along the edges of the region. A cluster is accepted as background when
    /// it shows up on at least two sides or dominates the border — so a subject that touches only one edge
    /// (typical portrait cropped at the bottom) is not mistaken for background.
    /// </summary>
    private Lab[] SampleBorderModel()
    {
        int band = Math.Clamp(Math.Min(w, h) / 150, 1, 6);
        var samples = new List<(Lab lab, int side)>();
        int perSide = 4000;

        void AddRect(int x0, int y0, int x1, int y1, int side)
        {
            long area = (long)(x1 - x0) * (y1 - y0);
            if (area <= 0) return;
            int step = Math.Max(1, (int)Math.Sqrt(area / (double)perSide));
            for (int y = y0; y < y1; y += step)
            {
                for (int x = x0; x < x1; x += step)
                {
                    int i = y * w + x;
                    if (!Opaque(i)) continue;
                    samples.Add((LabAt(i), side));
                }
            }
        }

        AddRect(0, 0, w, Math.Min(h, band), 0);
        AddRect(0, Math.Max(0, h - band), w, h, 1);
        AddRect(0, 0, Math.Min(w, band), h, 2);
        AddRect(Math.Max(0, w - band), 0, w, h, 3);

        if (samples.Count == 0)
        {
            return new[] { new Lab(1f, 0f, 0f) };
        }

        int k = Math.Clamp(samples.Count / 40, 1, 6);
        var centers = KMeans(samples.Select(p => p.lab).ToArray(), k);

        int kk = centers.Length;
        var count = new int[kk];
        var sideCount = new int[kk, 4];
        var sideTotal = new int[4];
        foreach (var (lab, side) in samples)
        {
            int c = Nearest(centers, lab);
            count[c]++;
            sideCount[c, side]++;
            sideTotal[side]++;
        }

        var accepted = new List<Lab>();
        int largest = Array.IndexOf(count, count.Max());
        for (int c = 0; c < kk; c++)
        {
            if (count[c] == 0) continue;
            double share = count[c] / (double)samples.Count;
            int sides = 0;
            for (int sd = 0; sd < 4; sd++)
            {
                if (sideTotal[sd] > 0 && sideCount[c, sd] >= 0.08 * sideTotal[sd]) sides++;
            }
            bool ok = c == largest || (share >= 0.02 && (sides >= 2 || share >= 0.35));
            if (ok) accepted.Add(centers[c]);
        }
        return accepted.ToArray();
    }

    private static int Nearest(Lab[] centers, Lab p)
    {
        int best = 0;
        float bd = float.MaxValue;
        for (int c = 0; c < centers.Length; c++)
        {
            float dl = p.L - centers[c].L, da = p.A - centers[c].A, db = p.B - centers[c].B;
            float d = dl * dl + da * da + db * db;
            if (d < bd)
            {
                bd = d;
                best = c;
            }
        }
        return best;
    }

    private static Lab[] KMeans(Lab[] pts, int k)
    {
        var rng = new Random(1234567);
        var centers = new List<Lab> { pts[rng.Next(pts.Length)] };
        var d2 = new float[pts.Length];
        while (centers.Count < k)
        {
            double sum = 0;
            for (int i = 0; i < pts.Length; i++)
            {
                var c = centers[Nearest(centers.ToArray(), pts[i])];
                float dl = pts[i].L - c.L, da = pts[i].A - c.A, db = pts[i].B - c.B;
                d2[i] = dl * dl + da * da + db * db;
                sum += d2[i];
            }
            if (sum <= 1e-12) break;
            double pick = rng.NextDouble() * sum;
            int chosen = pts.Length - 1;
            for (int i = 0; i < pts.Length; i++)
            {
                pick -= d2[i];
                if (pick <= 0)
                {
                    chosen = i;
                    break;
                }
            }
            centers.Add(pts[chosen]);
        }

        var cs = centers.ToArray();
        var assign = new int[pts.Length];
        for (int iter = 0; iter < 15; iter++)
        {
            bool changed = false;
            for (int i = 0; i < pts.Length; i++)
            {
                int c = Nearest(cs, pts[i]);
                if (c != assign[i] || iter == 0)
                {
                    changed |= c != assign[i];
                    assign[i] = c;
                }
            }
            var sl = new double[cs.Length]; var sa = new double[cs.Length]; var sb = new double[cs.Length];
            var cnt = new int[cs.Length];
            for (int i = 0; i < pts.Length; i++)
            {
                int c = assign[i];
                sl[c] += pts[i].L; sa[c] += pts[i].A; sb[c] += pts[i].B; cnt[c]++;
            }
            for (int c = 0; c < cs.Length; c++)
            {
                if (cnt[c] > 0) cs[c] = new Lab((float)(sl[c] / cnt[c]), (float)(sa[c] / cnt[c]), (float)(sb[c] / cnt[c]));
            }
            if (!changed && iter > 0) break;
        }
        return cs;
    }

    // ------------------------------------------------------------------ distance & clean plate

    private float Dist(float L, float A, float B, in Lab k)
    {
        float dl = (L - k.L) * lumaW, da = A - k.A, db = B - k.B;
        return MathF.Sqrt(dl * dl + da * da + db * db);
    }

    private void ComputeGlobalDistance(float[] dist)
    {
        float[] L = labL!, A = labA!, B = labB!;
        var m = model;
        Parallel.For(0, h, po, y =>
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int i = row + x;
                float best = float.MaxValue;
                for (int k = 0; k < m.Length; k++)
                {
                    float d = Dist(L[i], A[i], B[i], m[k]);
                    if (d < best) best = d;
                }
                dist[i] = best;
            }
        });
    }

    /// <summary>
    /// Estimates what the background looks like behind the subject ("clean plate") by interpolating confidently
    /// detected background pixels. Distances measured against the local clean plate make uneven screens,
    /// vignetting, gradients and soft shadows on the backdrop disappear cleanly.
    /// </summary>
    private void BuildCleanPlate(float[] dist)
    {
        // Blocks of at least 8 px so that thin structures (hair) never dominate a block's statistics.
        pf = Math.Max(8, (int)Math.Ceiling(Math.Max(w, h) / 512.0));
        int f = (int)pf;
        pw = (w + f - 1) / f;
        ph = (h + f - 1) / f;
        int pn = pw * ph;

        var avgL = new float[pn]; var avgA = new float[pn]; var avgB = new float[pn];
        var seedL = new float[pn]; var seedA = new float[pn]; var seedB = new float[pn];
        var weight = new float[pn];
        float seedThr = tol + 0.25f * soft;
        float[] L = labL!, A = labA!, B = labB!;

        // Chroma keys: keep the seeds with the strongest screen dominance (hair, smoke and other thin partial
        // occluders lower the dominance, the bare screen does not). Auto: component-wise median.
        bool chroma = false, dr = false, dg = false, db = false;
        if (chromaMode)
        {
            model[0].ToLinear(out float kr, out float kg, out float kb);
            chroma = KeyChannels(kr, kg, kb, out dr, out dg, out db, out _);
        }

        Parallel.For(0, ph, po, () => (new float[f * f], new float[f * f], new float[f * f], new float[f * f], new int[f * f]), (by, _, buf) =>
        {
            var (bl, ba, bb, key, idx) = buf;
            for (int bx = 0; bx < pw; bx++)
            {
                double al = 0, aa = 0, ab = 0;
                int all = 0, seeds = 0;
                for (int y = by * f; y < Math.Min(h, by * f + f); y++)
                {
                    for (int x = bx * f; x < Math.Min(w, bx * f + f); x++)
                    {
                        int i = y * w + x;
                        al += L[i]; aa += A[i]; ab += B[i]; all++;
                        if (dist[i] < seedThr && Opaque(i))
                        {
                            bl[seeds] = L[i]; ba[seeds] = A[i]; bb[seeds] = B[i];
                            if (chroma)
                            {
                                ColorMath.PixelToLinear(px[i], out float r, out float g, out float b);
                                key[seeds] = -Dominance(r, g, b, dr, dg, db);
                            }
                            seeds++;
                        }
                    }
                }
                int pi = by * pw + bx;
                avgL[pi] = (float)(al / all); avgA[pi] = (float)(aa / all); avgB[pi] = (float)(ab / all);
                if (seeds == 0) continue;
                weight[pi] = seeds / (float)all;
                if (chroma)
                {
                    for (int k = 0; k < seeds; k++) idx[k] = k;
                    Array.Sort(key, idx, 0, seeds);
                    int take = Math.Max(1, (seeds + 1) / 2);
                    double sl = 0, sa = 0, sb = 0;
                    for (int k = 0; k < take; k++)
                    {
                        sl += bl[idx[k]]; sa += ba[idx[k]]; sb += bb[idx[k]];
                    }
                    seedL[pi] = (float)(sl / take); seedA[pi] = (float)(sa / take); seedB[pi] = (float)(sb / take);
                }
                else
                {
                    seedL[pi] = MedianOf(bl, seeds);
                    seedA[pi] = MedianOf(ba, seeds);
                    seedB[pi] = MedianOf(bb, seeds);
                }
            }
            return buf;
        }, _ => { });

        if (keepEnclosed) KeepBorderConnected(weight);

        float[] cl = null!, ca = null!, cb = null!;
        const int Iterations = 3;
        for (int iter = 0; iter < Iterations; iter++)
        {
            ct.ThrowIfCancellationRequested();
            cl = (float[])seedL.Clone();
            ca = (float[])seedA.Clone();
            cb = (float[])seedB.Clone();
            if (!ImageOps.PushPull(new[] { cl, ca, cb }, weight, pw, ph))
            {
                return; // no background found at all
            }
            if (iter == Iterations - 1) break;

            // Grow the seed set into blocks that match the interpolated plate (gradients, vignetting).
            bool grew = false;
            for (int pi = 0; pi < pn; pi++)
            {
                if (weight[pi] >= 0.5f) continue;
                var p = new Lab(cl[pi], ca[pi], cb[pi]);
                if (Dist(avgL[pi], avgA[pi], avgB[pi], p) < seedThr * 0.8f)
                {
                    weight[pi] = 1f;
                    seedL[pi] = avgL[pi]; seedA[pi] = avgA[pi]; seedB[pi] = avgB[pi];
                    grew = true;
                }
            }
            if (!grew) break;
            if (keepEnclosed) KeepBorderConnected(weight);
        }

        plateL = cl;
        plateA = ca;
        plateB = cb;
        plateR = new float[pn];
        plateG = new float[pn];
        plateBl = new float[pn];
        for (int pi = 0; pi < pn; pi++)
        {
            ColorMath.OklabToLinear(cl[pi], ca[pi], cb[pi], out plateR[pi], out plateG[pi], out plateBl[pi]);
        }
        Debug?.Invoke("plateL", cl, pw, ph);
        Debug?.Invoke("plateWeight", weight, pw, ph);

        float pfl = pf;
        Parallel.For(0, h, po, y =>
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int i = row + x;
                var p = new Lab(
                    ImageOps.SampleBilinear(cl, pw, ph, pfl, x, y),
                    ImageOps.SampleBilinear(ca, pw, ph, pfl, x, y),
                    ImageOps.SampleBilinear(cb, pw, ph, pfl, x, y));
                float d = Dist(L[i], A[i], B[i], p);
                if (d < dist[i]) dist[i] = d;
            }
        });
    }

    private void KeepBorderConnected(float[] weight)
    {
        var pass = new byte[weight.Length];
        for (int i = 0; i < pass.Length; i++) pass[i] = weight[i] > 0 ? (byte)1 : (byte)0;
        var reached = ImageOps.FloodFromBorder(pass, pw, ph);
        for (int i = 0; i < pass.Length; i++)
        {
            if (reached[i] == 0) weight[i] = 0;
        }
    }

    /// <summary>Clean-plate colour (linear RGB) at a full resolution pixel.</summary>
    private bool TryPlate(int x, int y, out float r, out float g, out float b)
    {
        if (plateR == null)
        {
            r = g = b = 0f;
            return false;
        }
        r = ImageOps.SampleBilinear(plateR, pw, ph, pf, x, y);
        g = ImageOps.SampleBilinear(plateG!, pw, ph, pf, x, y);
        b = ImageOps.SampleBilinear(plateBl!, pw, ph, pf, x, y);
        return true;
    }

    /// <summary>Dominant channels of a saturated key (G for green, B for blue, G+B for cyan …). False for greyish keys.</summary>
    private static bool KeyChannels(float kr, float kg, float kb, out bool dr, out bool dg, out bool db, out float saturation)
    {
        float mx = MathF.Max(kr, MathF.Max(kg, kb));
        float mean = (kr + kg + kb) / 3f;
        float cr = kr - mean, cg = kg - mean, cb = kb - mean;
        float m = MathF.Max(cr, MathF.Max(cg, cb));
        saturation = mx > 1e-4f ? m / mx : 0f;
        float th = 0.5f * m;
        dr = cr > th;
        dg = cg > th;
        db = cb > th;
        return saturation >= 0.2f && !(dr && dg && db);
    }

    /// <summary>"Screen dominance": how much the key channel(s) exceed the strongest other channel.</summary>
    private static float Dominance(float r, float g, float b, bool dr, bool dg, bool db)
    {
        float minDom = float.MaxValue, maxOther = 0f;
        if (dr) minDom = MathF.Min(minDom, r); else maxOther = MathF.Max(maxOther, r);
        if (dg) minDom = MathF.Min(minDom, g); else maxOther = MathF.Max(maxOther, g);
        if (db) minDom = MathF.Min(minDom, b); else maxOther = MathF.Max(maxOther, b);
        return minDom - maxOther;
    }

    /// <summary>
    /// Colour-difference matte (the principle behind Keylight / IBK) computed in linear light against the local clean
    /// plate: alpha = 1 - dominance(pixel) / dominance(screen). Unlike a colour-distance threshold this is linear in
    /// coverage, so a hair strand covering 30 % of a pixel really gets alpha ≈ 0.3. Colours that are clearly far from
    /// the key (Oklab distance) are protected so greenish-but-different subject colours stay solid.
    /// </summary>
    private bool ColorDifferenceAlpha(float[] dist)
    {
        model[0].ToLinear(out float kr, out float kg, out float kb);
        if (!KeyChannels(kr, kg, kb, out bool dr, out bool dg, out bool db, out _)) return false;
        float eKey = Dominance(kr, kg, kb, dr, dg, db);
        if (eKey <= 1e-4f) return false;

        double t = Math.Clamp(s.Tolerance, 0, 100) / 100.0;
        double so = Math.Clamp(s.Softness, 0, 100) / 100.0;
        float lo = (float)(0.02 + 0.68 * Math.Pow(t, 1.5));
        float hi = (float)(1.0 - 0.25 * (1 - so) * (1 - so));
        if (hi < lo + 0.02f) hi = lo + 0.02f;
        float inv = 1f / (hi - lo);
        float p0 = tol + 2f * soft, p1 = tol + 4f * soft;

        Parallel.For(0, h, po, y =>
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int i = row + x;
                float e = eKey;
                if (TryPlate(x, y, out float pr, out float pg, out float pb))
                {
                    float ep = Dominance(pr, pg, pb, dr, dg, db);
                    // Trust the local plate only while it still looks like the screen.
                    e = MathF.Max(ep, 0.25f * eKey);
                }
                ColorMath.PixelToLinear(px[i], out float r, out float g, out float b);
                float a = 1f - Dominance(r, g, b, dr, dg, db) / e;
                a = ColorMath.Clamp01((a - lo) * inv);
                float protect = ColorMath.SmoothStep(p0, p1, dist[i]);
                alpha[i] = MathF.Max(a, protect);
            }
        });
        return true;
    }

    /// <summary>
    /// Auto mode: the tolerance must at least cover the backdrop's own variation (sensor noise, JPEG artefacts,
    /// texture), measured on the background part of the image border. Oklab exaggerates noise in dark areas,
    /// so a black backdrop otherwise leaves a speckled haze.
    /// </summary>
    private void AdaptToleranceToNoise(float[] dist)
    {
        // Pixel-to-pixel differences inside the background: smooth gradients contribute ~0, noise does not.
        int band = Math.Clamp(Math.Min(w, h) / 100, 2, 8);
        var samples = new List<float>();
        float limit = (tol + soft) * 4f;
        float[] L = labL!, A = labA!, B = labB!;
        void Sample(int i, int j)
        {
            if (!Opaque(i) || !Opaque(j) || dist[i] >= limit || dist[j] >= limit) return;
            samples.Add(Dist(L[i], A[i], B[i], new Lab(L[j], A[j], B[j])));
        }
        int stepX = Math.Max(1, w / 2000), stepY = Math.Max(1, h / 2000);
        for (int b = 0; b < band; b++)
        {
            for (int x = 0; x + 1 < w; x += stepX)
            {
                Sample(b * w + x, b * w + x + 1);
                Sample((h - 1 - b) * w + x, (h - 1 - b) * w + x + 1);
            }
            for (int y = 0; y + 1 < h; y += stepY)
            {
                Sample(y * w + b, (y + 1) * w + b);
                Sample(y * w + w - 1 - b, (y + 1) * w + w - 1 - b);
            }
        }
        if (samples.Count < 50) return;
        samples.Sort();
        float p90 = samples[(int)(samples.Count * 0.9)];
        // Never let a noisy border swallow the subject.
        tol = MathF.Max(tol, MathF.Min(1.5f * p90, 0.08f));
    }

    // ------------------------------------------------------------------ matte cleanup

    /// <summary>
    /// Background-like regions that are not connected to the border belong to the subject (e.g. a white shirt on a
    /// white backdrop). Walls are taken from the solid part of the subject only, so pockets of background between
    /// hair strands or fur are still removed.
    /// </summary>
    private void ProtectEnclosed()
    {
        var solid = new float[n];
        for (int i = 0; i < n; i++) solid[i] = alpha[i] >= 0.5f ? 1f : 0f;
        int open = Math.Clamp(Math.Min(w, h) / 300, 2, 6);
        ImageOps.Morph(solid, w, h, -open, ct);
        ImageOps.Morph(solid, w, h, open, ct);

        var pass = new byte[n];
        for (int i = 0; i < n; i++) pass[i] = solid[i] < 0.5f ? (byte)1 : (byte)0;
        var reached = ImageOps.FloodFromBorder(pass, w, h, BorderRuns(pass));

        var protect = new float[n];
        bool any = false;
        for (int i = 0; i < n; i++)
        {
            if (alpha[i] < 0.5f && reached[i] == 0)
            {
                protect[i] = 1f;
                any = true;
            }
        }
        if (!any) return;
        ImageOps.Morph(protect, w, h, 1, ct);
        ImageOps.BoxBlur(protect, w, h, 1, ct);
        for (int i = 0; i < n; i++)
        {
            if (protect[i] > alpha[i]) alpha[i] = protect[i];
        }
        protectMask = protect;
    }

    /// <summary>
    /// Core matte: the inside of the subject is always fully opaque. Colour keys cannot tell a grey, green-lit
    /// subject from a mix of subject and screen pixel by pixel, which leaves see-through patches in fur, skin or
    /// clothes; spatially, anything well inside the solid silhouette is subject. Only a band of a few pixels along
    /// the outline keeps its soft, partial alpha (hair, fur, motion blur).
    /// </summary>
    private void FillSolidCore()
    {
        int rc = Math.Clamp((int)Math.Round(Math.Min(w, h) / 120.0), 2, 12);
        var core = new float[n];
        for (int i = 0; i < n; i++) core[i] = alpha[i] >= 0.3f ? 1f : 0f;
        // Close see-through patches inside the silhouette before eroding — but only ones that are not clean
        // background, so the gaps between hair strands stay open.
        ImageOps.FillHoles(core, alpha, w, h, 64 * rc * rc, 0.12f, ct);
        ImageOps.Morph(core, w, h, -rc, ct);
        ImageOps.Morph(core, w, h, rc / 2, ct);
        ImageOps.BoxBlur(core, w, h, 1, ct);
        for (int i = 0; i < n; i++)
        {
            if (core[i] > alpha[i]) alpha[i] = core[i];
        }
    }

    /// <summary>
    /// Border pixels that start a flood: only runs of background that are long enough. A subject cropped by the
    /// image edge then cannot "leak" background into itself through its own short bright stripes at the edge.
    /// </summary>
    private byte[] BorderRuns(byte[] pass)
    {
        var seed = new byte[n];
        void Side(int count, Func<int, int> index)
        {
            int minRun = Math.Max(4, count / 50);
            int run = 0;
            for (int k = 0; k <= count; k++)
            {
                if (k < count && pass[index(k)] != 0)
                {
                    run++;
                    continue;
                }
                if (run >= minRun)
                {
                    for (int j = k - run; j < k; j++) seed[index(j)] = 1;
                }
                run = 0;
            }
        }
        Side(w, x => x);
        Side(w, x => (h - 1) * w + x);
        Side(h, y => y * w);
        Side(h, y => y * w + w - 1);
        return seed;
    }

    private byte[] Band(float lo, float hi, int dilate, out bool any, float[]? dist = null, float near = 0f)
    {
        var m = new float[n];
        int cnt = 0;
        for (int i = 0; i < n; i++)
        {
            if ((alpha[i] > lo && alpha[i] < hi) || (dist != null && alpha[i] >= hi && dist[i] < near))
            {
                m[i] = 1f;
                cnt++;
            }
        }
        any = cnt > 0;
        var band = new byte[n];
        if (!any) return band;
        if (dilate > 0) ImageOps.Morph(m, w, h, dilate, ct);
        for (int i = 0; i < n; i++) band[i] = m[i] > 0 ? (byte)1 : (byte)0;
        return band;
    }

    /// <summary>
    /// Colour-line alpha: in the transition band, a pixel is modelled as a mix of the local subject colour F and
    /// the local background B, and alpha is its position on the line B→F. This recovers true partial coverage
    /// of hair strands, motion blur and semi-transparent material.
    /// </summary>
    private const float ApClip = 0.02f;

    private void ProjectEdges(float[]? dist)
    {
        // Transition pixels, plus opaque pixels whose colour is still close to the background
        // (mixtures with a bright subject reach "opaque" distances long before full coverage).
        // The colour-difference matte of the chroma modes is already linear, so there it is only refined in the transition.
        float near = chromaMode ? 0f : tol + soft;
        // Plain backdrops: widen the band, since Oklab exaggerates small brightness differences near black and
        // white, a fibre covering a fraction of a pixel can look fully opaque by colour distance alone.
        int widen = (chromaMode ? 2 : 2 + (int)Math.Round(4 * Math.Clamp(s.EdgeDetail / 100.0, 0, 1))) + (int)MathF.Round(edgeScale);
        var band = Band(0.004f, 0.996f, widen, out bool any, dist, near);
        if (!any) return;

        // Local subject colour: interpolate the colours of confidently opaque pixels (push-pull at low resolution).
        // Using only confident pixels keeps the estimate independent from the (possibly too low) soft alpha.
        int f = Math.Max(1, (int)Math.Ceiling(Math.Max(w, h) / 512.0));
        int lw = (w + f - 1) / f, lh = (h + f - 1) / f, ln = lw * lh;
        var fr0 = new float[ln]; var fg0 = new float[ln]; var fb0 = new float[ln]; var wt = new float[ln];
        Parallel.For(0, lh, po, by =>
        {
            for (int bx = 0; bx < lw; bx++)
            {
                double r = 0, g = 0, b = 0;
                int c = 0, all = 0;
                for (int y = by * f; y < Math.Min(h, by * f + f); y++)
                {
                    for (int x = bx * f; x < Math.Min(w, bx * f + f); x++)
                    {
                        int i = y * w + x;
                        all++;
                        if (alpha[i] < 0.98f) continue;
                        ColorMath.PixelToLinear(px[i], out float vr, out float vg, out float vb);
                        r += vr; g += vg; b += vb; c++;
                    }
                }
                if (c == 0) continue;
                int li = by * lw + bx;
                fr0[li] = (float)(r / c); fg0[li] = (float)(g / c); fb0[li] = (float)(b / c); wt[li] = c / (float)all;
            }
        });
        if (!ImageOps.PushPull(new[] { fr0, fg0, fb0 }, wt, lw, lh)) return;

        // Local background colour: the clean plate, the key, or the background interpolated the same way.
        float[]? br0 = null, bg0 = null, bb0 = null;
        float keyR = 0, keyG = 0, keyB = 0;
        bool constantKey = plateR == null && chromaMode;
        if (constantKey)
        {
            model[0].ToLinear(out keyR, out keyG, out keyB);
        }
        else if (plateL == null)
        {
            br0 = new float[ln]; bg0 = new float[ln]; bb0 = new float[ln];
            var bw = new float[ln];
            Parallel.For(0, lh, po, by =>
            {
                for (int bx = 0; bx < lw; bx++)
                {
                    double r = 0, g = 0, b = 0;
                    int c = 0, all = 0;
                    for (int y = by * f; y < Math.Min(h, by * f + f); y++)
                    {
                        for (int x = bx * f; x < Math.Min(w, bx * f + f); x++)
                        {
                            int i = y * w + x;
                            all++;
                            if (alpha[i] > 0.02f) continue;
                            ColorMath.PixelToLinear(px[i], out float vr, out float vg, out float vb);
                            r += vr; g += vg; b += vb; c++;
                        }
                    }
                    if (c == 0) continue;
                    int li = by * lw + bx;
                    br0[li] = (float)(r / c); bg0[li] = (float)(g / c); bb0[li] = (float)(b / c); bw[li] = c / (float)all;
                }
            });
            if (!ImageOps.PushPull(new[] { br0, bg0, bb0 }, bw, lw, lh)) return;
        }

        // Plain backdrops: the linear colour-line alpha is the physically right estimate in the band, trust it
        // fully from "edge detail" 50 up. Chroma keys already have a linear matte, so it is only blended in.
        float mix = (float)Math.Clamp(s.EdgeDetail / (chromaMode ? 100.0 : 50.0), 0, 1);
        float ff = f;
        var result = (float[])alpha.Clone();
        Parallel.For(0, h, po, y =>
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int i = row + x;
                if (band[i] == 0) continue;
                ColorMath.PixelToLinear(px[i], out float cr, out float cg, out float cb);
                float fr = ImageOps.SampleBilinear(fr0, lw, lh, ff, x, y);
                float fg = ImageOps.SampleBilinear(fg0, lw, lh, ff, x, y);
                float fb = ImageOps.SampleBilinear(fb0, lw, lh, ff, x, y);
                float br, bgc, bb;
                if (constantKey)
                {
                    br = keyR; bgc = keyG; bb = keyB;
                }
                else if (TryPlate(x, y, out br, out bgc, out bb))
                {
                }
                else
                {
                    br = ImageOps.SampleBilinear(br0!, lw, lh, ff, x, y);
                    bgc = ImageOps.SampleBilinear(bg0!, lw, lh, ff, x, y);
                    bb = ImageOps.SampleBilinear(bb0!, lw, lh, ff, x, y);
                }
                float dr = fr - br, dg = fg - bgc, db = fb - bb;
                float n2 = dr * dr + dg * dg + db * db;
                if (n2 < 1e-6f) continue;
                float ap = ((cr - br) * dr + (cg - bgc) * dg + (cb - bb) * db) / n2;
                ap = ColorMath.Clamp01((ap - ApClip) / (1f - 2f * ApClip));
                float conf = n2 / (n2 + 0.01f);
                float a0 = alpha[i];
                float refined = conf * ap + (1f - conf) * a0;
                result[i] = a0 + (refined - a0) * mix;
            }
        });
        alpha = result;
    }

    private void RefineWithGuidedFilter()
    {
        int r = Math.Clamp(Math.Max(s.RefineRadius, (int)MathF.Round(edgeScale)), 1, 50);
        var band = Band(0.01f, 0.99f, r, out bool any);
        if (!any) return;
        double m = Math.Clamp(s.EdgeDetail / 100.0, 0, 1);
        float eps = (float)Math.Pow(10, -1.5 - 2.5 * m);
        var before = (float[])alpha.Clone();
        GuidedFilter.Apply(px, alpha, band, w, h, r, eps, ct);
        // The filter fits a linear model per window; on clean background (or solid subject) next to an edge
        // that leaves a faint haze. Only let it change confident pixels when it means it.
        Parallel.For(0, h, po, y =>
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int i = row + x;
                if (before[i] <= 0f && alpha[i] < 0.15f) alpha[i] = 0f;
                else if (before[i] >= 1f && alpha[i] > 0.85f) alpha[i] = 1f;
            }
        });
    }

    private void AdjustMatte()
    {
        float cb = (float)(Math.Clamp(s.ClipBlack, 0, 100) / 100.0 * 0.95);
        float cw = (float)(1.0 - Math.Clamp(s.ClipWhite, 0, 100) / 100.0 * 0.95);
        if (cw < cb + 0.01f) cw = cb + 0.01f;
        if (cb > 0f || cw < 1f)
        {
            float inv = 1f / (cw - cb);
            Parallel.For(0, h, po, y =>
            {
                int row = y * w;
                for (int x = 0; x < w; x++) alpha[row + x] = ColorMath.Clamp01((alpha[row + x] - cb) * inv);
            });
        }
        ImageOps.Morph(alpha, w, h, Math.Clamp(s.ShrinkGrow, -50, 50), ct);
        ImageOps.GaussianBlur(alpha, w, h, Math.Clamp(s.Feather, 0, 100), ct);
    }

    // ------------------------------------------------------------------ colour & output

    private uint[] Compose()
    {
        var output = new uint[n];
        OutputMode mode = s.Output;
        bool invert = s.Invert;
        float decon = (float)Math.Clamp(s.Decontaminate / 100.0, 0, 1);
        float despill = (float)Math.Clamp(s.Despill / 100.0, 0, 1);
        float shadowAmt = invert ? 0f : (float)Math.Clamp(s.Shadows / 100.0, 0, 1);

        ForegroundEstimator.Planes? fp = null, bp = null;
        if (mode != OutputMode.Matte && decon > 0f)
        {
            fp = new ForegroundEstimator.Planes(n);
            bp = new ForegroundEstimator.Planes(n);
            var active = Band(0f, 1f, 3, out _);
            ForegroundEstimator.Estimate(new PixelSource(px, w), alpha, w, h, fp, bp, ct, active);
            Mark("foreground");
        }

        float[]? shadow = shadowAmt > 0f && mode != OutputMode.Matte ? BuildShadowMap(shadowAmt) : null;

        // Despill reference colour.
        bool constantKey = plateR == null && chromaMode;
        float kR = 0, kG = 0, kB = 0;
        if (constantKey) model[0].ToLinear(out kR, out kG, out kB);
        bool restrictHue = !chromaMode; // in Auto mode only despill green / blue backdrops

        int bdR = (s.BackdropRgb >> 16) & 0xFF, bdG = (s.BackdropRgb >> 8) & 0xFF, bdB = s.BackdropRgb & 0xFF;

        Parallel.For(0, h, po, y =>
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int i = row + x;
                uint p = px[i];
                int srcA = (int)(p >> 24);
                float a = alpha[i];
                ColorMath.PixelToLinear(p, out float cr, out float cg, out float cb);

                float outR, outG, outB, outA;
                if (!invert)
                {
                    float fr = cr, fg = cg, fb = cb;
                    if (fp != null && a < 0.999f)
                    {
                        fr += (fp.R[i] - cr) * decon;
                        fg += (fp.G[i] - cg) * decon;
                        fb += (fp.B[i] - cb) * decon;
                    }
                    if (despill > 0f && a > 0f)
                    {
                        float kr, kg, kb;
                        if (constantKey)
                        {
                            kr = kR; kg = kG; kb = kB;
                        }
                        else if (TryPlate(x, y, out kr, out kg, out kb))
                        {
                        }
                        else if (bp != null)
                        {
                            kr = bp.R[i]; kg = bp.G[i]; kb = bp.B[i];
                        }
                        else if (model.Length > 0)
                        {
                            model[0].ToLinear(out kr, out kg, out kb);
                        }
                        else
                        {
                            kr = kg = kb = 0f;
                        }
                        Despill(ref fr, ref fg, ref fb, kr, kg, kb, despill, restrictHue);
                    }
                    float so = shadow != null ? shadow[i] * (1f - a) : 0f;
                    outA = a + so;
                    if (outA > 1e-6f)
                    {
                        float k = a / outA;
                        outR = fr * k; outG = fg * k; outB = fb * k;
                    }
                    else
                    {
                        outR = cr; outG = cg; outB = cb;
                    }
                }
                else
                {
                    outA = 1f - a;
                    outR = cr; outG = cg; outB = cb;
                    if (decon > 0f && a > 0f && TryPlate(x, y, out float pr, out float pg, out float pb))
                    {
                        // The clean plate is the best estimate of the background behind the subject.
                        outR += (pr - cr) * decon;
                        outG += (pg - cg) * decon;
                        outB += (pb - cb) * decon;
                    }
                    else if (bp != null)
                    {
                        outR += (bp.R[i] - cr) * decon;
                        outG += (bp.G[i] - cg) * decon;
                        outB += (bp.B[i] - cb) * decon;
                    }
                }

                outA *= srcA / 255f;
                int ab = (int)(ColorMath.Clamp01(outA) * 255f + 0.5f);

                switch (mode)
                {
                    case OutputMode.Matte:
                        output[i] = ColorMath.Pack(ab, ab, ab, 255);
                        break;
                    case OutputMode.OverColor:
                    {
                        int r8 = ColorMath.LinearToByte(outR), g8 = ColorMath.LinearToByte(outG), b8 = ColorMath.LinearToByte(outB);
                        output[i] = ColorMath.Pack(
                            (r8 * ab + bdR * (255 - ab) + 127) / 255,
                            (g8 * ab + bdG * (255 - ab) + 127) / 255,
                            (b8 * ab + bdB * (255 - ab) + 127) / 255,
                            255);
                        break;
                    }
                    case OutputMode.Overlay:
                    {
                        ColorMath.Unpack(p, out int r8, out int g8, out int b8, out _);
                        int t = (255 - ab) * 160 / 255;
                        output[i] = ColorMath.Pack(
                            (r8 * (255 - t) + 255 * t) / 255,
                            (g8 * (255 - t) + 32 * t) / 255,
                            (b8 * (255 - t) + 64 * t) / 255,
                            255);
                        break;
                    }
                    default:
                        output[i] = ColorMath.Pack(ColorMath.LinearToByte(outR), ColorMath.LinearToByte(outG), ColorMath.LinearToByte(outB), ab);
                        break;
                }
            }
        });
        return output;
    }

    /// <summary>
    /// Generalised "limit" despill. The key's dominant channel(s) (G for green, B for blue, G+B for cyan …) are
    /// not allowed to exceed the strongest remaining channel; the removed energy is given back as neutral
    /// luminance so the subject does not get darker.
    /// </summary>
    private static void Despill(ref float r, ref float g, ref float b, float kr, float kg, float kb, float strength, bool restrictHue)
    {
        float mx = MathF.Max(kr, MathF.Max(kg, kb));
        if (mx <= 1e-4f) return;
        float mean = (kr + kg + kb) / 3f;
        float cr = kr - mean, cg = kg - mean, cb = kb - mean;
        float m = MathF.Max(cr, MathF.Max(cg, cb));
        float sat = m / mx;
        float gate = ColorMath.SmoothStep(0.15f, 0.35f, sat);
        if (gate <= 0f) return;
        float th = 0.5f * m;
        bool dr = cr > th, dg = cg > th, db = cb > th;
        if (dr && dg && db) return;
        if (restrictHue && dr) return;

        float minDom = float.MaxValue, maxOther = 0f, lumDom = 0f;
        if (dr) { minDom = MathF.Min(minDom, r); lumDom += 0.2126f; } else maxOther = MathF.Max(maxOther, r);
        if (dg) { minDom = MathF.Min(minDom, g); lumDom += 0.7152f; } else maxOther = MathF.Max(maxOther, g);
        if (db) { minDom = MathF.Min(minDom, b); lumDom += 0.0722f; } else maxOther = MathF.Max(maxOther, b);

        float e = minDom - maxOther;
        if (e <= 0f) return;
        e *= strength * gate;
        if (dr) r -= e;
        if (dg) g -= e;
        if (db) b -= e;
        float back = e * lumDom * 0.6f;
        r += back;
        g += back;
        b += back;
    }

    /// <summary>Shadow opacity for background pixels: how much darker they are than the clean backdrop.</summary>
    private float[] BuildShadowMap(float amount)
    {
        float yRef = 0f;
        foreach (var m in model)
        {
            m.ToLinear(out float r, out float g, out float b);
            yRef = MathF.Max(yRef, ColorMath.Luminance(r, g, b));
        }
        if (yRef <= 1e-4f) return new float[n];
        var map = new float[n];
        Parallel.For(0, h, po, y =>
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int i = row + x;
                if (alpha[i] >= 1f) continue;
                ColorMath.PixelToLinear(px[i], out float r, out float g, out float b);
                float dark = 1f - ColorMath.Luminance(r, g, b) / yRef;
                map[i] = ColorMath.Clamp01((dark - 0.06f) / 0.94f) * amount;
            }
        });
        ImageOps.BoxBlur(map, w, h, 1, ct);
        return map;
    }
}

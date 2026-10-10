using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace UltraKey.Core.Ai;

/// <summary>
/// Subject segmentation for photos with arbitrary backgrounds: BiRefNet (downloaded model, ONNX Runtime) or the
/// embedded U²-Net. Produces a soft subject mask at full resolution; the keyer then refines its edges on the real
/// pixels. The network result is cached per image, so changing other settings does not run it again.
/// </summary>
public static class AiSegmenter
{
    private const string ResourceName = "UltraKey.u2net.ukm";
    private static readonly Lazy<U2Net?> Model = new(LoadModel, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly object CacheSync = new();
    private static readonly object NetSync = new();

    private static readonly List<(AiModel model, int w, int h, ulong hash, int x0, int y0, int cw, int ch, float[] mask)> cache = new();
    private static readonly HashSet<string> broken = new();
    private static BiRefNet? birefnet;
    private static string? birefnetPath;
    private static int busy;

    // The session holds a lot of (video) memory; let it go when the effect has not been used for a while.
    private const int IdleMilliseconds = 120_000;
    private static readonly Timer IdleTimer = new(_ => ReleaseIfIdle(), null, Timeout.Infinite, Timeout.Infinite);

    public static bool IsAvailable => Model.Value != null || Resolve(AiModel.BiRefNet) != AiModel.U2Net;

    /// <summary>Timing output for the test harness.</summary>
    public static Action<string>? Trace { get; set; }

    /// <summary>Why BiRefNet could not be used (runtime or model failed to load), for the UI.</summary>
    public static string? LoadError { get; private set; }

    /// <summary>Whether the last BiRefNet run used the GPU (null before the first run).</summary>
    public static bool? LastRunOnGpu { get; private set; }

    /// <summary>Why the last BiRefNet run did not use the GPU.</summary>
    public static string? GpuError { get; private set; }

    /// <summary>The model that will actually run for the requested one: a missing BiRefNet model falls back.</summary>
    public static AiModel Resolve(AiModel requested)
    {
        if (requested == AiModel.U2Net) return AiModel.U2Net;
        AiModel other = requested == AiModel.BiRefNet ? AiModel.BiRefNetLite : AiModel.BiRefNet;
        foreach (AiModel m in new[] { requested, other })
        {
            string? path = Models.Find(m);
            if (path == null) continue;
            lock (NetSync)
            {
                if (!broken.Contains(path)) return m;
            }
        }
        return AiModel.U2Net;
    }

    /// <summary>Network input resolution, which sets how coarse the mask is relative to the image.</summary>
    public static int Resolution(AiModel used) => used == AiModel.U2Net ? 320 : 1024;

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

    /// <summary>
    /// The BiRefNet session for this model file, created on first use and kept for later runs. Pair every
    /// non-null result with <see cref="ReleaseBiRefNet"/>.
    /// </summary>
    private static BiRefNet? AcquireBiRefNet(string path)
    {
        lock (NetSync)
        {
            BiRefNet? net = GetBiRefNet(path);
            if (net != null) busy++;
            return net;
        }
    }

    private static void ReleaseBiRefNet()
    {
        lock (NetSync)
        {
            busy--;
            IdleTimer.Change(IdleMilliseconds, Timeout.Infinite);
        }
    }

    private static void ReleaseIfIdle()
    {
        lock (NetSync)
        {
            if (busy > 0 || birefnet == null) return;
            birefnet.Dispose();
            birefnet = null;
            birefnetPath = null;
        }
    }

    private static BiRefNet? GetBiRefNet(string path)
    {
        lock (NetSync)
        {
            if (birefnet != null && birefnetPath == path) return birefnet;
            birefnet?.Dispose(); // only one model in (video) memory at a time
            birefnet = null;
            birefnetPath = null;
            if (broken.Contains(path)) return null;
            if (!OnnxRuntimeLoader.EnsureLoaded())
            {
                LoadError = OnnxRuntimeLoader.Error;
                broken.Add(path);
                return null;
            }
            try
            {
                long start = Environment.TickCount64;
                birefnet = CreateBiRefNet(path);
                Trace?.Invoke($"BiRefNet load: {Environment.TickCount64 - start} ms");
                birefnetPath = path;
                LoadError = null;
                return birefnet;
            }
            catch (Exception ex)
            {
                LoadError = ex.Message;
                broken.Add(path);
                return null;
            }
        }
    }

    // Kept out of line so that nothing touches ONNX Runtime types before the runtime is loaded.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static BiRefNet CreateBiRefNet(string path) => BiRefNet.Load(path);

    /// <summary>Returns the subject probability (0..1) for every pixel, or null when no model is available.</summary>
    public static float[]? Segment(uint[] px, int w, int h, AiModel model, CancellationToken ct, out AiModel used) =>
        Segment(px, w, h, 0, 0, w, h, model, ct, out used);

    /// <summary>Segments only the crop (x0, y0, cw, ch); the result has the crop's size.</summary>
    public static float[]? Segment(uint[] px, int w, int h, int x0, int y0, int cw, int ch, AiModel model, CancellationToken ct, out AiModel used)
    {
        used = Resolve(model);
        ulong hash = Hash(px, w, h);
        lock (CacheSync)
        {
            foreach (var c in cache)
            {
                if (c.model == used && c.w == w && c.h == h && c.hash == hash && c.x0 == x0 && c.y0 == y0 && c.cw == cw && c.ch == ch) return c.mask;
            }
        }

        var crop = new uint[cw * ch];
        for (int y = 0; y < ch; y++) Array.Copy(px, (y0 + y) * w + x0, crop, y * cw, cw);

        float[]? map = null;
        int s = 0;
        bool normalise = false;
        if (used != AiModel.U2Net)
        {
            string path = Models.Find(used)!;
            BiRefNet? birefnetNet = AcquireBiRefNet(path);
            if (birefnetNet != null)
            {
                try
                {
                    s = birefnetNet.InputSize;
                    long start = Environment.TickCount64;
                    map = birefnetNet.Run(Prepare(crop, cw, ch, s, byImageMax: false), ct);
                    Trace?.Invoke($"BiRefNet run: {Environment.TickCount64 - start} ms");
                    ct.ThrowIfCancellationRequested();
                    LastRunOnGpu = birefnetNet.OnGpu;
                    GpuError = birefnetNet.OnGpu ? null : BiRefNet.GpuError;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Do not fail the effect: report it and use U²-Net from now on.
                    lock (NetSync)
                    {
                        LoadError = ex.Message;
                        broken.Add(path);
                    }
                    map = null;
                }
                finally
                {
                    ReleaseBiRefNet();
                }
            }
        }
        if (map == null)
        {
            U2Net? net = Model.Value;
            if (net == null) return null;
            used = AiModel.U2Net;
            s = net.InputSize;
            map = net.Run(Prepare(crop, cw, ch, s, byImageMax: true), ct);
            normalise = true; // U²-Net's output is relative; rembg normalises it the same way
        }

        float mn = 0f, mx = 1f;
        if (normalise)
        {
            mn = float.MaxValue;
            mx = float.MinValue;
            foreach (float v in map)
            {
                if (v < mn) mn = v;
                if (v > mx) mx = v;
            }
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
            cache.Add((used, w, h, hash, x0, y0, cw, ch, mask));
            if (cache.Count > 6) cache.RemoveAt(0);
        }
        return mask;
    }

    /// <summary>
    /// Finds the object under (x, y) that the full-image pass missed (e.g. a logo next to more salient objects):
    /// the network is re-run on progressively larger crops centred on the point until the object containing the
    /// point fits inside the crop. Returns a full-size mask (0 outside the object), or null if nothing is found.
    /// </summary>
    public static float[]? ObjectAt(uint[] px, int w, int h, int x, int y, AiModel model, CancellationToken ct)
    {
        float[]? best = null;
        foreach (double f in new[] { 0.35, 0.5, 0.7, 1.0 })
        {
            int cw = Math.Max(16, (int)(w * f)), ch = Math.Max(16, (int)(h * f));
            cw = Math.Min(cw, w);
            ch = Math.Min(ch, h);
            int x0 = Math.Clamp(x - cw / 2, 0, w - cw), y0 = Math.Clamp(y - ch / 2, 0, h - ch);
            float[]? m = Segment(px, w, h, x0, y0, cw, ch, model, ct, out _);
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

    /// <summary>Area-averaged resize to s×s, scaled to 0..1 (by the image maximum for U²-Net), ImageNet mean/std, CHW.</summary>
    private static float[] Prepare(uint[] px, int w, int h, int s, bool byImageMax)
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
        float max = 255f;
        if (byImageMax)
        {
            max = 1e-6f;
            foreach (float v in rgb) max = MathF.Max(max, v);
        }
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

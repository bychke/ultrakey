using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Threading;
using System.Threading.Tasks;

namespace UltraKey.Core.Ai;

/// <summary>
/// Minimal, dependency-free inference engine for U²-Net salient object segmentation
/// (Qin et al., "U²-Net: Going Deeper with Nested U-Structure for Salient Object Detection", 2020, Apache-2.0).
/// Reads the compact UKM2 format produced by tools/model/convert_u2net.py. Convolutions run as packed
/// im2col × GEMM with an AVX2/FMA micro-kernel (scalar fallback elsewhere).
/// </summary>
internal sealed class U2Net
{
    private const int MR = 6;   // micro-kernel rows (output channels)
    private const int NR = 16;  // micro-kernel columns (pixels)
    private const int NC = 256; // pixels per task

    private abstract record Op;
    private sealed record ConvOp(int In, int Out, int Cin, int Cout, int K, int Dil, bool Relu, float[] PackedW, float[] Bias) : Op;
    private sealed record PoolOp(int In, int Out) : Op;
    private sealed record ResizeOp(int In, int Out, int Ref) : Op;
    private sealed record ConcatOp(int Out, int[] Ins) : Op;
    private sealed record AddOp(int A, int B, int Out) : Op;
    private sealed record SigmoidOp(int In, int Out) : Op;

    private sealed class Tensor
    {
        public float[]? Data;
        public int C, H, W;
    }

    private readonly Op[] ops;
    private readonly int tensorCount;
    private readonly int outputTensor;

    public int InputSize { get; }

    private U2Net(Op[] ops, int tensorCount, int outputTensor, int inputSize)
    {
        this.ops = ops;
        this.tensorCount = tensorCount;
        this.outputTensor = outputTensor;
        InputSize = inputSize;
    }

    public static U2Net Load(Stream stream)
    {
        using var r = new BinaryReader(stream);
        if (new string(r.ReadChars(4)) != "UKM2") throw new InvalidDataException("Not a UltraKey model.");
        int size = r.ReadInt32(), tensors = r.ReadInt32(), output = r.ReadInt32(), count = r.ReadInt32();
        var ops = new Op[count];
        for (int i = 0; i < count; i++)
        {
            int type = r.ReadInt32();
            ops[i] = type switch
            {
                1 => ReadConv(r),
                2 => new PoolOp(r.ReadInt32(), r.ReadInt32()),
                3 => new ResizeOp(r.ReadInt32(), r.ReadInt32(), r.ReadInt32()),
                4 => ReadConcat(r),
                5 => new AddOp(r.ReadInt32(), r.ReadInt32(), r.ReadInt32()),
                6 => new SigmoidOp(r.ReadInt32(), r.ReadInt32()),
                _ => throw new InvalidDataException("Unknown op " + type),
            };
        }
        return new U2Net(ops, tensors, output, size);
    }

    private static ConcatOp ReadConcat(BinaryReader r)
    {
        int output = r.ReadInt32(), n = r.ReadInt32();
        var ins = new int[n];
        for (int i = 0; i < n; i++) ins[i] = r.ReadInt32();
        return new ConcatOp(output, ins);
    }

    private static ConvOp ReadConv(BinaryReader r)
    {
        int input = r.ReadInt32(), output = r.ReadInt32(), cin = r.ReadInt32(), cout = r.ReadInt32();
        int k = r.ReadInt32(), dil = r.ReadInt32();
        bool relu = r.ReadInt32() != 0;
        var bias = new float[cout];
        for (int i = 0; i < cout; i++) bias[i] = r.ReadSingle();
        int kk = cin * k * k;
        byte[] raw = r.ReadBytes(cout * kk * 2);
        var q = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Half>(raw);

        // Pack for the micro-kernel: blocks of MR output channels, K-major, MR contiguous values per k.
        int blocks = (cout + MR - 1) / MR;
        var packed = new float[blocks * kk * MR];
        for (int o = 0; o < cout; o++)
        {
            int b = o / MR, lane = o % MR;
            int src = o * kk;
            int dst = b * kk * MR + lane;
            for (int j = 0; j < kk; j++) packed[dst + j * MR] = (float)q[src + j];
        }
        return new ConvOp(input, output, cin, cout, k, dil, relu, packed, bias);
    }

    /// <summary>Runs the network on a CHW input (3 × InputSize × InputSize) and returns the saliency map.</summary>
    public float[] Run(float[] input, CancellationToken ct)
    {
        // Last use of every tensor so intermediate results can be released early.
        var lastUse = new int[tensorCount];
        for (int i = 0; i < ops.Length; i++)
        {
            foreach (int t in Inputs(ops[i])) lastUse[t] = i;
        }
        lastUse[outputTensor] = int.MaxValue;

        var tensors = new Tensor[tensorCount];
        for (int i = 0; i < tensorCount; i++) tensors[i] = new Tensor();
        tensors[0].Data = input;
        tensors[0].C = 3;
        tensors[0].H = tensors[0].W = InputSize;

        for (int i = 0; i < ops.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            Execute(ops[i], tensors, ct);
            foreach (int t in Inputs(ops[i]))
            {
                if (lastUse[t] == i && t != outputTensor) tensors[t].Data = null; // keep the shape for Resize refs
            }
        }
        return tensors[outputTensor].Data!;
    }

    private static IEnumerable<int> Inputs(Op op) => op switch
    {
        ConvOp c => new[] { c.In },
        PoolOp p => new[] { p.In },
        ResizeOp r => new[] { r.In },
        ConcatOp c => c.Ins,
        AddOp a => new[] { a.A, a.B },
        SigmoidOp s => new[] { s.In },
        _ => Array.Empty<int>(),
    };

    private static void Execute(Op op, Tensor[] t, CancellationToken ct)
    {
        switch (op)
        {
            case ConvOp c:
                Conv(c, t[c.In], t[c.Out], ct);
                break;
            case PoolOp p:
                MaxPool(t[p.In], t[p.Out]);
                break;
            case ResizeOp r:
                Resize(t[r.In], t[r.Out], t[r.Ref].H, t[r.Ref].W);
                break;
            case ConcatOp c:
            {
                var o = t[c.Out];
                o.H = t[c.Ins[0]].H;
                o.W = t[c.Ins[0]].W;
                o.C = 0;
                foreach (int i in c.Ins) o.C += t[i].C;
                o.Data = new float[o.C * o.H * o.W];
                int offset = 0;
                foreach (int i in c.Ins)
                {
                    var s = t[i].Data!;
                    Array.Copy(s, 0, o.Data, offset, s.Length);
                    offset += s.Length;
                }
                break;
            }
            case AddOp a:
            {
                var x = t[a.A];
                var y = t[a.B].Data!;
                var o = t[a.Out];
                o.C = x.C; o.H = x.H; o.W = x.W;
                var d = new float[x.Data!.Length];
                for (int i = 0; i < d.Length; i++) d[i] = x.Data[i] + y[i];
                o.Data = d;
                break;
            }
            case SigmoidOp s:
            {
                var x = t[s.In];
                var o = t[s.Out];
                o.C = x.C; o.H = x.H; o.W = x.W;
                var d = new float[x.Data!.Length];
                for (int i = 0; i < d.Length; i++) d[i] = 1f / (1f + MathF.Exp(-x.Data[i]));
                o.Data = d;
                break;
            }
        }
    }

    private static void MaxPool(Tensor x, Tensor o)
    {
        int h = x.H, w = x.W, oh = (h + 1) / 2, ow = (w + 1) / 2;
        o.C = x.C; o.H = oh; o.W = ow;
        var src = x.Data!;
        var d = new float[x.C * oh * ow];
        Parallel.For(0, x.C, c =>
        {
            int sb = c * h * w, db = c * oh * ow;
            for (int y = 0; y < oh; y++)
            {
                int y0 = 2 * y, y1 = Math.Min(h - 1, y0 + 1);
                for (int xx = 0; xx < ow; xx++)
                {
                    int x0 = 2 * xx, x1 = Math.Min(w - 1, x0 + 1);
                    float m = MathF.Max(MathF.Max(src[sb + y0 * w + x0], src[sb + y0 * w + x1]),
                                        MathF.Max(src[sb + y1 * w + x0], src[sb + y1 * w + x1]));
                    d[db + y * ow + xx] = m;
                }
            }
        });
        o.Data = d;
    }

    /// <summary>Bilinear resize, ONNX "pytorch_half_pixel" coordinates.</summary>
    private static void Resize(Tensor x, Tensor o, int oh, int ow)
    {
        int h = x.H, w = x.W;
        o.C = x.C; o.H = oh; o.W = ow;
        var src = x.Data!;
        var d = new float[x.C * oh * ow];
        float sy = (float)h / oh, sx = (float)w / ow;
        Parallel.For(0, x.C, c =>
        {
            int sb = c * h * w, db = c * oh * ow;
            for (int y = 0; y < oh; y++)
            {
                float fy = oh > 1 ? MathF.Max(0f, (y + 0.5f) * sy - 0.5f) : 0f;
                int y0 = Math.Min(h - 1, (int)fy), y1 = Math.Min(h - 1, y0 + 1);
                float ty = fy - y0;
                for (int xx = 0; xx < ow; xx++)
                {
                    float fx = ow > 1 ? MathF.Max(0f, (xx + 0.5f) * sx - 0.5f) : 0f;
                    int x0 = Math.Min(w - 1, (int)fx), x1 = Math.Min(w - 1, x0 + 1);
                    float tx = fx - x0;
                    float a = src[sb + y0 * w + x0] + (src[sb + y0 * w + x1] - src[sb + y0 * w + x0]) * tx;
                    float b = src[sb + y1 * w + x0] + (src[sb + y1 * w + x1] - src[sb + y1 * w + x0]) * tx;
                    d[db + y * ow + xx] = a + (b - a) * ty;
                }
            }
        });
        o.Data = d;
    }

    private static unsafe void Conv(ConvOp c, Tensor x, Tensor o, CancellationToken ct)
    {
        int h = x.H, w = x.W, n = h * w;
        int K = c.Cin * c.K * c.K;
        o.C = c.Cout; o.H = h; o.W = w;
        var output = new float[c.Cout * n];
        o.Data = output;
        var input = x.Data!;

        int mBlocks = (c.Cout + MR - 1) / MR;
        int mGroup = Math.Max(1, Math.Min(mBlocks, 16));           // micro-blocks per task
        int mGroups = (mBlocks + mGroup - 1) / mGroup;
        int nChunks = (n + NC - 1) / NC;
        var opts = new ParallelOptions { CancellationToken = ct };

        Parallel.For(0, mGroups * nChunks, opts, () => new float[K * NC], (task, _, panel) =>
        {
            int nc = task % nChunks, mg = task / nChunks;
            int n0 = nc * NC, ncols = Math.Min(NC, n - n0);
            int panels = (ncols + NR - 1) / NR;
            PackB(input, panel, c.Cin, c.K, c.Dil, h, w, n0, ncols, panels, K);

            fixed (float* pw = c.PackedW, pb = panel, po = output, pbias = c.Bias)
            {
                int mb0 = mg * mGroup, mb1 = Math.Min(mBlocks, mb0 + mGroup);
                for (int mb = mb0; mb < mb1; mb++)
                {
                    float* a = pw + (long)mb * K * MR;
                    int m0 = mb * MR, rows = Math.Min(MR, c.Cout - m0);
                    for (int p = 0; p < panels; p++)
                    {
                        int cols = Math.Min(NR, ncols - p * NR);
                        Kernel(a, pb + (long)p * K * NR, K, po, n, m0, rows, n0 + p * NR, cols, pbias, c.Relu);
                    }
                }
            }
            return panel;
        }, _ => { });
    }

    /// <summary>im2col of NC output pixels, packed as panels of NR pixels × K.</summary>
    private static void PackB(float[] input, float[] panel, int cin, int k, int dil, int h, int w, int n0, int ncols, int panels, int K)
    {
        int r = k / 2;
        for (int p = 0; p < panels; p++)
        {
            int baseOut = p * K * NR;
            for (int j = 0; j < NR; j++)
            {
                int col = p * NR + j;
                if (col >= ncols)
                {
                    for (int kk = 0; kk < K; kk++) panel[baseOut + kk * NR + j] = 0f;
                    continue;
                }
                int pix = n0 + col;
                int y = pix / w, xx = pix % w;
                int kk2 = 0;
                for (int ci = 0; ci < cin; ci++)
                {
                    int cb = ci * h * w;
                    for (int ky = -r; ky <= r; ky++)
                    {
                        int sy = y + ky * dil;
                        bool rowOk = (uint)sy < (uint)h;
                        for (int kx = -r; kx <= r; kx++)
                        {
                            int sx = xx + kx * dil;
                            panel[baseOut + kk2 * NR + j] = rowOk && (uint)sx < (uint)w ? input[cb + sy * w + sx] : 0f;
                            kk2++;
                        }
                    }
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static unsafe void Kernel(float* a, float* b, int K, float* output, int n, int m0, int rows, int col0, int cols, float* bias, bool relu)
    {
        float* acc = stackalloc float[MR * NR];
        if (Avx.IsSupported)
        {
            Vector256<float> c00 = Vector256<float>.Zero, c01 = c00, c10 = c00, c11 = c00, c20 = c00, c21 = c00;
            Vector256<float> c30 = c00, c31 = c00, c40 = c00, c41 = c00, c50 = c00, c51 = c00;
            bool fma = Fma.IsSupported;
            for (int k = 0; k < K; k++)
            {
                Vector256<float> b0 = Avx.LoadVector256(b + k * NR);
                Vector256<float> b1 = Avx.LoadVector256(b + k * NR + 8);
                float* ak = a + k * MR;
                if (fma)
                {
                    Vector256<float> v = Vector256.Create(ak[0]); c00 = Fma.MultiplyAdd(v, b0, c00); c01 = Fma.MultiplyAdd(v, b1, c01);
                    v = Vector256.Create(ak[1]); c10 = Fma.MultiplyAdd(v, b0, c10); c11 = Fma.MultiplyAdd(v, b1, c11);
                    v = Vector256.Create(ak[2]); c20 = Fma.MultiplyAdd(v, b0, c20); c21 = Fma.MultiplyAdd(v, b1, c21);
                    v = Vector256.Create(ak[3]); c30 = Fma.MultiplyAdd(v, b0, c30); c31 = Fma.MultiplyAdd(v, b1, c31);
                    v = Vector256.Create(ak[4]); c40 = Fma.MultiplyAdd(v, b0, c40); c41 = Fma.MultiplyAdd(v, b1, c41);
                    v = Vector256.Create(ak[5]); c50 = Fma.MultiplyAdd(v, b0, c50); c51 = Fma.MultiplyAdd(v, b1, c51);
                }
                else
                {
                    Vector256<float> v = Vector256.Create(ak[0]); c00 = Avx.Add(c00, Avx.Multiply(v, b0)); c01 = Avx.Add(c01, Avx.Multiply(v, b1));
                    v = Vector256.Create(ak[1]); c10 = Avx.Add(c10, Avx.Multiply(v, b0)); c11 = Avx.Add(c11, Avx.Multiply(v, b1));
                    v = Vector256.Create(ak[2]); c20 = Avx.Add(c20, Avx.Multiply(v, b0)); c21 = Avx.Add(c21, Avx.Multiply(v, b1));
                    v = Vector256.Create(ak[3]); c30 = Avx.Add(c30, Avx.Multiply(v, b0)); c31 = Avx.Add(c31, Avx.Multiply(v, b1));
                    v = Vector256.Create(ak[4]); c40 = Avx.Add(c40, Avx.Multiply(v, b0)); c41 = Avx.Add(c41, Avx.Multiply(v, b1));
                    v = Vector256.Create(ak[5]); c50 = Avx.Add(c50, Avx.Multiply(v, b0)); c51 = Avx.Add(c51, Avx.Multiply(v, b1));
                }
            }
            Avx.Store(acc, c00); Avx.Store(acc + 8, c01);
            Avx.Store(acc + 16, c10); Avx.Store(acc + 24, c11);
            Avx.Store(acc + 32, c20); Avx.Store(acc + 40, c21);
            Avx.Store(acc + 48, c30); Avx.Store(acc + 56, c31);
            Avx.Store(acc + 64, c40); Avx.Store(acc + 72, c41);
            Avx.Store(acc + 80, c50); Avx.Store(acc + 88, c51);
        }
        else
        {
            for (int i = 0; i < MR * NR; i++) acc[i] = 0f;
            for (int k = 0; k < K; k++)
            {
                float* bk = b + k * NR;
                float* ak = a + k * MR;
                for (int i = 0; i < MR; i++)
                {
                    float av = ak[i];
                    float* row = acc + i * NR;
                    for (int j = 0; j < NR; j++) row[j] += av * bk[j];
                }
            }
        }

        for (int i = 0; i < rows; i++)
        {
            float bi = bias[m0 + i];
            float* dst = output + (long)(m0 + i) * n + col0;
            float* src = acc + i * NR;
            for (int j = 0; j < cols; j++)
            {
                float v = src[j] + bi;
                dst[j] = relu && v < 0f ? 0f : v;
            }
        }
    }
}

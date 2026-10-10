using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using UltraKey.Core;

// Usage: UltraKey.Cli <input.pam> <output.pam> [Property=Value ...]
// Properties are the names of KeyerSettings members, e.g. Mode=GreenScreen Tolerance=25 Output=Matte.
if (args.Length == 4 && args[0] == "u2net")
{
    // u2net <model.ukm> <input.f32 (3x320x320)> <output.f32>
    using var ms = File.OpenRead(args[1]);
    var net = UltraKey.Core.Ai.U2Net.Load(ms);
    byte[] raw = File.ReadAllBytes(args[2]);
    var inp = new float[raw.Length / 4];
    Buffer.BlockCopy(raw, 0, inp, 0, raw.Length);
    var t = Stopwatch.StartNew();
    var res = net.Run(inp, default);
    Console.WriteLine($"u2net inference {t.ElapsedMilliseconds} ms");
    var outBytes = new byte[res.Length * 4];
    Buffer.BlockCopy(res, 0, outBytes, 0, outBytes.Length);
    File.WriteAllBytes(args[3], outBytes);
    return 0;
}

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: UltraKey.Cli <input.pam> <output.pam> [Property=Value ...]");
    return 2;
}

var (pixels, w, h) = Pam.Read(args[0]);
var settings = new KeyerSettings();
for (int i = 2; i < args.Length; i++)
{
    var kv = args[i].Split('=', 2);
    var prop = typeof(KeyerSettings).GetProperty(kv[0]) ?? throw new ArgumentException("Unknown setting " + kv[0]);
    object value = prop.PropertyType.IsEnum
        ? Enum.Parse(prop.PropertyType, kv[1], ignoreCase: true)
        : prop.PropertyType == typeof(int) && kv[1].StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? Convert.ToInt32(kv[1], 16)
            : Convert.ChangeType(kv[1], prop.PropertyType, CultureInfo.InvariantCulture);
    settings = settings with { };
    prop.SetValue(settings, value);
}

string? dbg = Environment.GetEnvironmentVariable("ULTRAKEY_DEBUG_DIR");
if (dbg != null)
{
    KeyerRun.Debug = (name, data, dw, dh) =>
    {
        using var bw = new BinaryWriter(File.Create(Path.Combine(dbg, name + ".f32")));
        bw.Write(dw);
        bw.Write(dh);
        foreach (float v in data) bw.Write(v);
    };
}

if (Environment.GetEnvironmentVariable("ULTRAKEY_TRACE") != null)
{
    KeyerRun.Trace = Console.WriteLine;
    UltraKey.Core.Ai.AiSegmenter.Trace = Console.WriteLine;
}

var sw = Stopwatch.StartNew();
var result = Keyer.Process(pixels, w, h, settings) ?? throw new InvalidOperationException("cancelled");
Console.WriteLine($"{w}x{h} processed in {sw.ElapsedMilliseconds} ms");
if (UltraKey.Core.Ai.AiSegmenter.LastRunOnGpu is bool gpu) Console.WriteLine("BiRefNet ran on the " + (gpu ? "GPU" : "CPU"));
if (UltraKey.Core.Ai.AiSegmenter.LoadError is string error) Console.WriteLine("BiRefNet: " + error);
if (UltraKey.Core.Ai.AiSegmenter.GpuError is string gpuError) Console.WriteLine("GPU: " + gpuError);
Pam.Write(args[1], result, w, h);
return 0;

internal static class Pam
{
    public static (uint[] px, int w, int h) Read(string path)
    {
        using var fs = File.OpenRead(path);
        int w = 0, h = 0, depth = 0;
        while (true)
        {
            string line = ReadLine(fs);
            if (line == "ENDHDR") break;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            switch (parts[0])
            {
                case "WIDTH": w = int.Parse(parts[1]); break;
                case "HEIGHT": h = int.Parse(parts[1]); break;
                case "DEPTH": depth = int.Parse(parts[1]); break;
            }
        }
        var data = new byte[w * h * depth];
        fs.ReadExactly(data);
        var px = new uint[w * h];
        for (int i = 0; i < w * h; i++)
        {
            int o = i * depth;
            byte a = depth == 4 ? data[o + 3] : (byte)255;
            px[i] = (uint)data[o + 2] | ((uint)data[o + 1] << 8) | ((uint)data[o] << 16) | ((uint)a << 24);
        }
        return (px, w, h);
    }

    public static void Write(string path, uint[] px, int w, int h)
    {
        using var fs = File.Create(path);
        var header = Encoding.ASCII.GetBytes($"P7\nWIDTH {w}\nHEIGHT {h}\nDEPTH 4\nMAXVAL 255\nTUPLTYPE RGB_ALPHA\nENDHDR\n");
        fs.Write(header);
        var data = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            uint p = px[i];
            data[i * 4] = (byte)(p >> 16);
            data[i * 4 + 1] = (byte)(p >> 8);
            data[i * 4 + 2] = (byte)p;
            data[i * 4 + 3] = (byte)(p >> 24);
        }
        fs.Write(data);
    }

    private static string ReadLine(Stream s)
    {
        var sb = new StringBuilder();
        int c;
        while ((c = s.ReadByte()) >= 0 && c != '\n') sb.Append((char)c);
        return sb.ToString();
    }
}

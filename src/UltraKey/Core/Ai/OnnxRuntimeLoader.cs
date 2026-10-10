using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace UltraKey.Core.Ai;

/// <summary>
/// Makes ONNX Runtime usable from inside Paint.NET. Paint.NET loads only the plugin DLL, so the runtime
/// (managed wrapper, onnxruntime.dll with DirectML, DirectML.dll) is embedded gzip-compressed and unpacked once to
/// %LOCALAPPDATA%\UltraKey\runtime\&lt;version&gt;. The native DLLs are loaded by full path first, so the wrapper's
/// DllImport("onnxruntime") binds to them and onnxruntime.dll gets this DirectML.dll rather than the older
/// one in System32. Outside the plugin (the test harness) the runtime comes from NuGet as usual.
/// </summary>
internal static class OnnxRuntimeLoader
{
    private const string Prefix = "UltraKey.ort.";
    private const string ManagedName = "Microsoft.ML.OnnxRuntime";
    private static readonly object Sync = new();
    private static bool? ready;

    /// <summary>Why the runtime could not be loaded, for the UI.</summary>
    public static string? Error { get; private set; }

    public static bool EnsureLoaded()
    {
        lock (Sync)
        {
            ready ??= Load();
            return ready.Value;
        }
    }

    private static bool Load()
    {
        try
        {
            Assembly self = typeof(OnnxRuntimeLoader).Assembly;
            string[] names = self.GetManifestResourceNames();
            if (Array.IndexOf(names, Prefix + ManagedName + ".dll.gz") < 0)
            {
                return true; // not the plugin build: the runtime is a normal dependency
            }
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            {
                Error = "ONNX Runtime is bundled for Windows x64 only.";
                return false;
            }

            string version = self.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0";
            string dir = Path.Combine(Models.DataDirectory, "runtime", version.Split('+')[0]);
            Directory.CreateDirectory(dir);
            foreach (string res in names)
            {
                if (res.StartsWith(Prefix, StringComparison.Ordinal) && res.EndsWith(".gz", StringComparison.Ordinal))
                {
                    Extract(self, res, Path.Combine(dir, res[Prefix.Length..^3]));
                }
            }

            NativeLibrary.Load(Path.Combine(dir, "DirectML.dll"));
            NativeLibrary.Load(Path.Combine(dir, "onnxruntime.dll"));

            string managed = Path.Combine(dir, ManagedName + ".dll");
            AssemblyLoadContext context = AssemblyLoadContext.GetLoadContext(self) ?? AssemblyLoadContext.Default;
            Assembly ort = context.LoadFromAssemblyPath(managed);
            context.Resolving += (_, name) => name.Name == ManagedName ? ort : null;
            return true;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            return false;
        }
    }

    private static void Extract(Assembly self, string resource, string target)
    {
        using Stream gz = self.GetManifestResourceStream(resource)!;
        using var data = new MemoryStream();
        using (var unzip = new GZipStream(gz, CompressionMode.Decompress)) unzip.CopyTo(data);
        if (File.Exists(target) && new FileInfo(target).Length == data.Length) return;

        // Another Paint.NET instance may be unpacking at the same time: write aside, then move into place.
        string temp = target + "." + Environment.ProcessId + ".tmp";
        File.WriteAllBytes(temp, data.ToArray());
        try
        {
            File.Move(temp, target, overwrite: true);
        }
        catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && File.Exists(target))
        {
            File.Delete(temp); // the file is in use by another instance, so it is already there
        }
    }
}

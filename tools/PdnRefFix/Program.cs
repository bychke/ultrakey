using System;
using System.IO;
using Mono.Cecil;

// Paint.NET only loads a plugin when every PaintDotNet.* assembly it references has a version lower than or
// equal to the installed one. The compiler records the version of the DLLs we built against (e.g. 5.1.12 =
// 5.112.x), which locks out users of earlier 5.1.x releases. This rewrites those references to a minimum
// version (default 5.100.0.0 = Paint.NET 5.1.0).
//
// Usage: PdnRefFix <plugin.dll> [minimumVersion]
if (args.Length < 1)
{
    Console.Error.WriteLine("usage: PdnRefFix <plugin.dll> [minimumVersion]");
    return 2;
}

string path = Path.GetFullPath(args[0]);
var minimum = Version.Parse(args.Length > 1 ? args[1] : "5.100.0.0");

byte[] original = File.ReadAllBytes(path);
using var input = new MemoryStream(original);
using var module = ModuleDefinition.ReadModule(input, new ReaderParameters { ReadSymbols = false });

int changed = 0;
foreach (AssemblyNameReference reference in module.AssemblyReferences)
{
    if (!reference.Name.StartsWith("PaintDotNet", StringComparison.OrdinalIgnoreCase)) continue;
    if (reference.Version <= minimum) continue;
    Console.WriteLine($"  {reference.Name}: {reference.Version} -> {minimum}");
    reference.Version = minimum;
    changed++;
}

if (changed > 0)
{
    module.Write(path);
}
Console.WriteLine($"PdnRefFix: {changed} reference(s) updated in {Path.GetFileName(path)}");
return 0;

using System;
using System.Reflection;
using PaintDotNet;

[assembly: PluginSupportInfo(typeof(UltraKey.PluginSupportInfo), DisplayName = "UltraKey")]

namespace UltraKey;

public sealed class PluginSupportInfo : IPluginSupportInfo
{
    public string DisplayName => Strings.EffectName;

    public string Author => "UltraKey contributors";

    public string Copyright => "MIT License";

    public Version Version => typeof(PluginSupportInfo).Assembly.GetName().Version ?? new Version(1, 0);

    public Uri WebsiteUri => new("https://github.com/bychke/paintnet-plugin");
}

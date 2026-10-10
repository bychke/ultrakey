using System;
using System.IO;

namespace UltraKey.Core.Ai;

/// <summary>Where the downloadable AI models live (install.ps1 / tools/model/get_birefnet.ps1 put them there).</summary>
public static class Models
{
    /// <summary>%LOCALAPPDATA%\UltraKey</summary>
    public static string DataDirectory =>
        Environment.GetEnvironmentVariable("ULTRAKEY_DATA_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UltraKey");

    public static string ModelDirectory => Path.Combine(DataDirectory, "models");

    /// <summary>File name of the ONNX model for a BiRefNet variant (both are the float16 exports, MIT license).</summary>
    public static string FileName(AiModel model) => model == AiModel.BiRefNetLite ? "birefnet-lite.onnx" : "birefnet.onnx";

    /// <summary>The model file, or null when it is not installed.</summary>
    public static string? Find(AiModel model)
    {
        if (model == AiModel.U2Net) return null;
        string path = Path.Combine(ModelDirectory, FileName(model));
        return File.Exists(path) ? path : null;
    }
}

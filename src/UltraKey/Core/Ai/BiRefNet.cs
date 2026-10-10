using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace UltraKey.Core.Ai;

/// <summary>
/// BiRefNet dichotomous image segmentation (Zheng et al., "Bilateral Reference for High-Resolution Dichotomous Image
/// Segmentation", 2024, MIT license) on ONNX Runtime: DirectML on any DirectX 12 GPU, CPU when that is not available
/// or runs out of video memory. Only use it after <see cref="OnnxRuntimeLoader.EnsureLoaded"/> returned true.
/// </summary>
internal sealed class BiRefNet : IDisposable
{
    private readonly string path;
    private InferenceSession session;
    private string inputName = "";
    private bool halfInput;

    public int InputSize { get; private set; } = 1024;

    public bool OnGpu { get; private set; }

    /// <summary>Why the GPU is not used, if it is not.</summary>
    public static string? GpuError { get; private set; }

    private BiRefNet(string path, InferenceSession session, bool onGpu)
    {
        this.path = path;
        this.session = session;
        OnGpu = onGpu;
        ReadMetadata();
    }

    public static BiRefNet Load(string path)
    {
        if (Environment.GetEnvironmentVariable("ULTRAKEY_CPU") == null)
        {
            try
            {
                return new BiRefNet(path, CreateSession(path, gpu: true), onGpu: true);
            }
            catch (Exception ex)
            {
                // No DirectX 12 device (or no DirectML): use the CPU.
                GpuError = ex.Message;
            }
        }
        return new BiRefNet(path, CreateSession(path, gpu: false), onGpu: false);
    }

    private static InferenceSession CreateSession(string path, bool gpu)
    {
        var options = new SessionOptions { LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR };
        try
        {
            if (gpu)
            {
                // DirectML wants these; whole-graph fusion also runs out of memory on 6 GB cards with this model.
                options.EnableMemoryPattern = false;
                options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                options.AddSessionConfigEntry("ep.dml.disable_graph_fusion", "1");
                options.AppendExecutionProvider_DML(0);
            }
            return new InferenceSession(path, options);
        }
        finally
        {
            options.Dispose();
        }
    }

    private void ReadMetadata()
    {
        foreach (KeyValuePair<string, NodeMetadata> input in session.InputMetadata)
        {
            inputName = input.Key;
            halfInput = input.Value.ElementDataType == TensorElementType.Float16;
            int[] dims = input.Value.Dimensions;
            if (dims.Length == 4 && dims[2] > 0) InputSize = dims[2];
            break;
        }
    }

    /// <summary>Runs the network on a normalised 3×s×s CHW image and returns the subject probability (s×s, 0..1).</summary>
    public float[]? Run(float[] chw, CancellationToken ct)
    {
        try
        {
            return RunOnce(chw, ct);
        }
        catch (OnnxRuntimeException ex) when (OnGpu && !ct.IsCancellationRequested)
        {
            // Typically out of video memory: carry on with the CPU for the rest of the session.
            session.Dispose();
            session = CreateSession(path, gpu: false);
            OnGpu = false;
            GpuError = ex.Message;
            return RunOnce(chw, ct);
        }
    }

    private float[]? RunOnce(float[] chw, CancellationToken ct)
    {
        int s = InputSize;
        long[] shape = { 1, 3, s, s };
        using var runOptions = new RunOptions();
        using CancellationTokenRegistration cancel = ct.Register(() => runOptions.Terminate = true);

        OrtValue input;
        if (halfInput)
        {
            var half = new Float16[chw.Length];
            for (int i = 0; i < chw.Length; i++) half[i] = (Float16)chw[i];
            input = OrtValue.CreateTensorValueFromMemory(half, shape);
        }
        else
        {
            input = OrtValue.CreateTensorValueFromMemory(chw, shape);
        }

        try
        {
            using IDisposableReadOnlyCollection<OrtValue> outputs = session.Run(runOptions, new[] { inputName }, new[] { input }, session.OutputNames);
            OrtValue output = outputs[0];
            var result = new float[s * s];
            if (output.GetTensorTypeAndShape().ElementDataType == TensorElementType.Float16)
            {
                ReadOnlySpan<Float16> logits = output.GetTensorDataAsSpan<Float16>();
                for (int i = 0; i < result.Length; i++) result[i] = Sigmoid((float)logits[i]);
            }
            else
            {
                ReadOnlySpan<float> logits = output.GetTensorDataAsSpan<float>();
                for (int i = 0; i < result.Length; i++) result[i] = Sigmoid(logits[i]);
            }
            return result;
        }
        catch (OnnxRuntimeException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            input.Dispose();
        }
    }

    private static float Sigmoid(float x) => 1f / (1f + MathF.Exp(-x));

    public void Dispose() => session.Dispose();
}

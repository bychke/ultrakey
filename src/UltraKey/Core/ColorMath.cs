using System;
using System.Runtime.CompilerServices;

namespace UltraKey.Core;

/// <summary>sRGB / linear / Oklab conversions. Pixels are packed BGRA (0xAARRGGBB as uint).</summary>
internal static class ColorMath
{
    public static readonly float[] SrgbToLinear = BuildSrgbToLinear();

    private const int LinearLutSize = 4096;
    private static readonly byte[] LinearToSrgbLut = BuildLinearToSrgb();

    private static float[] BuildSrgbToLinear()
    {
        var t = new float[256];
        for (int i = 0; i < 256; i++)
        {
            float c = i / 255f;
            t[i] = c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        }
        return t;
    }

    private static byte[] BuildLinearToSrgb()
    {
        var t = new byte[LinearLutSize + 1];
        for (int i = 0; i <= LinearLutSize; i++)
        {
            t[i] = (byte)Math.Clamp((int)MathF.Round(LinearToSrgb(i / (float)LinearLutSize) * 255f), 0, 255);
        }
        return t;
    }

    public static float LinearToSrgb(float v)
    {
        if (v <= 0f) return 0f;
        if (v >= 1f) return 1f;
        return v <= 0.0031308f ? v * 12.92f : 1.055f * MathF.Pow(v, 1f / 2.4f) - 0.055f;
    }

    /// <summary>Fast linear -> sRGB byte via a LUT; the steep dark end (below 1/64) uses the exact curve.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte LinearToByte(float v)
    {
        if (!(v > 0f)) return 0;
        if (v >= 1f) return 255;
        if (v < 1f / 64f)
        {
            return (byte)(LinearToSrgb(v) * 255f + 0.5f);
        }
        return LinearToSrgbLut[(int)(v * LinearLutSize + 0.5f)];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Unpack(uint p, out int r, out int g, out int b, out int a)
    {
        b = (int)(p & 0xFF);
        g = (int)((p >> 8) & 0xFF);
        r = (int)((p >> 16) & 0xFF);
        a = (int)(p >> 24);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Pack(int r, int g, int b, int a)
    {
        return (uint)b | ((uint)g << 8) | ((uint)r << 16) | ((uint)a << 24);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void PixelToLinear(uint p, out float r, out float g, out float b)
    {
        float[] lut = SrgbToLinear;
        b = lut[p & 0xFF];
        g = lut[(p >> 8) & 0xFF];
        r = lut[(p >> 16) & 0xFF];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LinearToOklab(float r, float g, float b, out float L, out float A, out float B)
    {
        float l = 0.4122214708f * r + 0.5363325363f * g + 0.0514459929f * b;
        float m = 0.2119034982f * r + 0.6806995451f * g + 0.1073969566f * b;
        float s = 0.0883024619f * r + 0.2817188376f * g + 0.6299787005f * b;
        l = MathF.Cbrt(l);
        m = MathF.Cbrt(m);
        s = MathF.Cbrt(s);
        L = 0.2104542553f * l + 0.7936177850f * m - 0.0040720468f * s;
        A = 1.9779984951f * l - 2.4285922050f * m + 0.4505937099f * s;
        B = 0.0259040371f * l + 0.7827717662f * m - 0.8086757660f * s;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void OklabToLinear(float L, float A, float B, out float r, out float g, out float b)
    {
        float l = L + 0.3963377774f * A + 0.2158037573f * B;
        float m = L - 0.1055613458f * A - 0.0638541728f * B;
        float s = L - 0.0894841775f * A - 1.2914855480f * B;
        l = l * l * l;
        m = m * m * m;
        s = s * s * s;
        r = 4.0767416621f * l - 3.3077115913f * m + 0.2309699292f * s;
        g = -1.2684380046f * l + 2.6097574011f * m - 0.3413193965f * s;
        b = -0.0041960863f * l - 0.7034186147f * m + 1.7076147010f * s;
    }

    public static Lab RgbToLab(int rgb)
    {
        float r = SrgbToLinear[(rgb >> 16) & 0xFF];
        float g = SrgbToLinear[(rgb >> 8) & 0xFF];
        float b = SrgbToLinear[rgb & 0xFF];
        LinearToOklab(r, g, b, out float L, out float A, out float B);
        return new Lab(L, A, B);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Luminance(float r, float g, float b) => 0.2126f * r + 0.7152f * g + 0.0722f * b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float SmoothStep(float e0, float e1, float x)
    {
        float t = (x - e0) / (e1 - e0);
        if (t <= 0f) return 0f;
        if (t >= 1f) return 1f;
        return t * t * (3f - 2f * t);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
}

internal readonly record struct Lab(float L, float A, float B)
{
    public float Chroma => MathF.Sqrt(A * A + B * B);

    public void ToLinear(out float r, out float g, out float b) => ColorMath.OklabToLinear(L, A, B, out r, out g, out b);
}

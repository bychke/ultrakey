namespace UltraKey.Core;

/// <summary>How the background (key) colour is determined.</summary>
public enum KeyMode
{
    /// <summary>Learn the background from the edges of the image / selection (any colour, gradients, studio sweeps).</summary>
    Auto = 0,
    GreenScreen = 1,
    BlueScreen = 2,
    CustomColor = 3,
    PrimaryColor = 4,
    PickPoint = 5,

    /// <summary>Neural subject segmentation (U²-Net) — photos with any background (rooms, floors, outdoors).</summary>
    AiSubject = 6,
}

/// <summary>What to do with background-coloured areas that are fully enclosed by the subject.</summary>
public enum EnclosedAreas
{
    /// <summary>Keep them in Auto mode, remove them in the chroma-key modes.</summary>
    Smart = 0,
    Remove = 1,
    Keep = 2,
}

public enum OutputMode
{
    Result = 0,
    Matte = 1,
    OverColor = 2,
    Overlay = 3,
}

/// <summary>All user-tweakable parameters. Percent values are 0..100.</summary>
public sealed record KeyerSettings
{
    public KeyMode Mode { get; init; } = KeyMode.AiSubject;

    /// <summary>0xRRGGBB</summary>
    public int CustomKeyRgb { get; init; } = 0x00B140;

    /// <summary>0xRRGGBB</summary>
    public int PrimaryRgb { get; init; }

    /// <summary>-1..1 relative to the processed region.</summary>
    public double PickX { get; init; }

    public double PickY { get; init; }

    /// <summary>AI mode: also keep the object under this point (-1..1 relative to the processed region).</summary>
    public bool KeepPointOn { get; init; }

    public double KeepX { get; init; }

    public double KeepY { get; init; }

    /// <summary>AI mode: remove the object under this point.</summary>
    public bool RemovePointOn { get; init; }

    public double RemoveX { get; init; }

    public double RemoveY { get; init; }

    public double Tolerance { get; init; } = 20;

    public double Softness { get; init; } = 30;

    public double LumaSensitivity { get; init; } = 40;

    public bool UnevenCorrection { get; init; } = true;

    public EnclosedAreas Enclosed { get; init; } = EnclosedAreas.Smart;

    /// <summary>Islands / holes smaller than this many pixels are cleaned up.</summary>
    public int SpeckSize { get; init; }

    /// <summary>Make the inside of the subject fully opaque; only the outline keeps partial alpha.</summary>
    public bool SolidCore { get; init; } = true;

    public double EdgeDetail { get; init; } = 60;

    public int RefineRadius { get; init; } = 3;

    /// <summary>Negative = shrink (choke), positive = grow, in pixels.</summary>
    public double ShrinkGrow { get; init; }

    /// <summary>Gaussian feather sigma in pixels.</summary>
    public double Feather { get; init; }

    public double ClipBlack { get; init; }

    public double ClipWhite { get; init; }

    public double Despill { get; init; } = 100;

    public double Decontaminate { get; init; } = 100;

    public double Shadows { get; init; }

    public bool Invert { get; init; }

    public OutputMode Output { get; init; } = OutputMode.Result;

    /// <summary>0xRRGGBB, used by <see cref="OutputMode.OverColor"/>.</summary>
    public int BackdropRgb { get; init; } = 0xFFFFFF;

    internal bool IsChromaMode => Mode != KeyMode.Auto;

    internal bool KeepEnclosed => Enclosed switch
    {
        EnclosedAreas.Keep => true,
        EnclosedAreas.Remove => false,
        _ => Mode == KeyMode.Auto,
    };
}

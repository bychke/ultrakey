using System;
using System.Collections.Generic;
using System.Drawing;
using PaintDotNet;
using PaintDotNet.Effects;
using PaintDotNet.Imaging;
using PaintDotNet.IndirectUI;
using PaintDotNet.PropertySystem;
using PaintDotNet.Rendering;
using UltraKey.Core;

namespace UltraKey;

public sealed class UltraKeyEffect : PropertyBasedBitmapEffect
{
    private enum PropertyNames
    {
        Mode,
        KeyColor,
        PickPoint,
        Tolerance,
        Softness,
        LumaSensitivity,
        UnevenCorrection,
        Enclosed,
        SpeckSize,
        EdgeDetail,
        RefineRadius,
        ShrinkGrow,
        Feather,
        ClipBlack,
        ClipWhite,
        Despill,
        Decontaminate,
        Shadows,
        Invert,
        Output,
        BackdropColor,
    }

    private readonly object sync = new();
    private KeyerSettings settings = new();
    private RectInt32 region;
    private uint[]? result;

    public UltraKeyEffect()
        : base(Strings.EffectName, LoadIcon(), SubmenuNames.Object, BitmapEffectOptions.Create() with { IsConfigurable = true })
    {
    }

    private static Image LoadIcon()
    {
        using var stream = typeof(UltraKeyEffect).Assembly.GetManifestResourceStream("UltraKey.Icon.png");
        if (stream == null) return new Bitmap(16, 16);
        // GDI+ needs the stream alive for the bitmap's lifetime, so return a detached copy.
        using var loaded = new Bitmap(stream);
        return new Bitmap(loaded);
    }

    protected override PropertyCollection OnCreatePropertyCollection()
    {
        var props = new List<Property>
        {
            StaticListChoiceProperty.CreateForEnum(PropertyNames.Mode, KeyMode.Auto),
            new Int32Property(PropertyNames.KeyColor, 0x00B140, 0, 0xFFFFFF),
            new DoubleVectorProperty(PropertyNames.PickPoint, new Vector2Double(-0.9, -0.9), new Vector2Double(-1, -1), new Vector2Double(1, 1)),
            new Int32Property(PropertyNames.Tolerance, 20, 0, 100),
            new Int32Property(PropertyNames.Softness, 30, 0, 100),
            new Int32Property(PropertyNames.LumaSensitivity, 40, 0, 100),
            new BooleanProperty(PropertyNames.UnevenCorrection, true),
            StaticListChoiceProperty.CreateForEnum(PropertyNames.Enclosed, EnclosedAreas.Smart),
            new Int32Property(PropertyNames.SpeckSize, 0, 0, 2000),
            new Int32Property(PropertyNames.EdgeDetail, 60, 0, 100),
            new Int32Property(PropertyNames.RefineRadius, 3, 1, 30),
            new DoubleProperty(PropertyNames.ShrinkGrow, 0, -20, 20),
            new DoubleProperty(PropertyNames.Feather, 0, 0, 20),
            new Int32Property(PropertyNames.ClipBlack, 0, 0, 100),
            new Int32Property(PropertyNames.ClipWhite, 0, 0, 100),
            new Int32Property(PropertyNames.Despill, 100, 0, 100),
            new Int32Property(PropertyNames.Decontaminate, 100, 0, 100),
            new Int32Property(PropertyNames.Shadows, 0, 0, 100),
            new BooleanProperty(PropertyNames.Invert, false),
            StaticListChoiceProperty.CreateForEnum(PropertyNames.Output, OutputMode.Result),
            new Int32Property(PropertyNames.BackdropColor, 0xFFFFFF, 0, 0xFFFFFF),
        };

        var rules = new List<PropertyCollectionRule>
        {
            new ReadOnlyBoundToValueRule<object, StaticListChoiceProperty>(PropertyNames.KeyColor, PropertyNames.Mode, KeyMode.CustomColor, inverse: true),
            new ReadOnlyBoundToValueRule<object, StaticListChoiceProperty>(PropertyNames.PickPoint, PropertyNames.Mode, KeyMode.PickPoint, inverse: true),
            new ReadOnlyBoundToValueRule<object, StaticListChoiceProperty>(PropertyNames.BackdropColor, PropertyNames.Output, OutputMode.OverColor, inverse: true),
            new ReadOnlyBoundToBooleanRule(PropertyNames.Shadows, PropertyNames.Invert, inverse: false),
        };

        return new PropertyCollection(props, rules);
    }

    protected override ControlInfo OnCreateConfigUI(PropertyCollection props)
    {
        ControlInfo ui = CreateDefaultConfigUI(props);

        void Name(PropertyNames p, string text) => ui.SetPropertyControlValue(p, ControlInfoPropertyNames.DisplayName, text);
        void Desc(PropertyNames p, string text) => ui.SetPropertyControlValue(p, ControlInfoPropertyNames.Description, text);

        Name(PropertyNames.Mode, Strings.Mode);
        var mode = ui.FindControlForPropertyName(PropertyNames.Mode)!;
        mode.SetValueDisplayName(KeyMode.Auto, Strings.ModeAuto);
        mode.SetValueDisplayName(KeyMode.GreenScreen, Strings.ModeGreen);
        mode.SetValueDisplayName(KeyMode.BlueScreen, Strings.ModeBlue);
        mode.SetValueDisplayName(KeyMode.CustomColor, Strings.ModeCustom);
        mode.SetValueDisplayName(KeyMode.PrimaryColor, Strings.ModePrimary);
        mode.SetValueDisplayName(KeyMode.PickPoint, Strings.ModePick);

        Name(PropertyNames.KeyColor, Strings.KeyColor);
        ui.SetPropertyControlType(PropertyNames.KeyColor, PropertyControlType.ColorWheel);

        Name(PropertyNames.PickPoint, Strings.PickPoint);
        ui.SetPropertyControlValue(PropertyNames.PickPoint, ControlInfoPropertyNames.SliderSmallChangeX, 0.01);
        ui.SetPropertyControlValue(PropertyNames.PickPoint, ControlInfoPropertyNames.SliderSmallChangeY, 0.01);
        ui.SetPropertyControlValue(PropertyNames.PickPoint, ControlInfoPropertyNames.UpDownIncrementX, 0.01);
        ui.SetPropertyControlValue(PropertyNames.PickPoint, ControlInfoPropertyNames.UpDownIncrementY, 0.01);
        ui.SetPropertyControlValue(PropertyNames.PickPoint, ControlInfoPropertyNames.DecimalPlaces, 3);

        Name(PropertyNames.Tolerance, Strings.Tolerance);
        Desc(PropertyNames.Tolerance, Strings.ToleranceHint);
        Name(PropertyNames.Softness, Strings.Softness);
        Name(PropertyNames.LumaSensitivity, Strings.Luma);
        Desc(PropertyNames.LumaSensitivity, Strings.LumaHint);

        Name(PropertyNames.UnevenCorrection, string.Empty);
        Desc(PropertyNames.UnevenCorrection, Strings.Uneven);

        Name(PropertyNames.Enclosed, Strings.Enclosed);
        var enclosed = ui.FindControlForPropertyName(PropertyNames.Enclosed)!;
        enclosed.SetValueDisplayName(EnclosedAreas.Smart, Strings.EnclosedSmart);
        enclosed.SetValueDisplayName(EnclosedAreas.Remove, Strings.EnclosedRemove);
        enclosed.SetValueDisplayName(EnclosedAreas.Keep, Strings.EnclosedKeep);

        Name(PropertyNames.SpeckSize, Strings.Specks);
        Name(PropertyNames.EdgeDetail, Strings.EdgeDetail);
        Name(PropertyNames.RefineRadius, Strings.RefineRadius);
        Name(PropertyNames.ShrinkGrow, Strings.ShrinkGrow);
        ui.SetPropertyControlValue(PropertyNames.ShrinkGrow, ControlInfoPropertyNames.DecimalPlaces, 1);
        Name(PropertyNames.Feather, Strings.Feather);
        ui.SetPropertyControlValue(PropertyNames.Feather, ControlInfoPropertyNames.DecimalPlaces, 1);
        Name(PropertyNames.ClipBlack, Strings.ClipBlack);
        Name(PropertyNames.ClipWhite, Strings.ClipWhite);
        Name(PropertyNames.Despill, Strings.Despill);
        Name(PropertyNames.Decontaminate, Strings.Decontaminate);
        Name(PropertyNames.Shadows, Strings.Shadows);

        Name(PropertyNames.Invert, string.Empty);
        Desc(PropertyNames.Invert, Strings.Invert);

        Name(PropertyNames.Output, Strings.Output);
        var output = ui.FindControlForPropertyName(PropertyNames.Output)!;
        output.SetValueDisplayName(OutputMode.Result, Strings.OutputResult);
        output.SetValueDisplayName(OutputMode.Matte, Strings.OutputMatte);
        output.SetValueDisplayName(OutputMode.OverColor, Strings.OutputOver);
        output.SetValueDisplayName(OutputMode.Overlay, Strings.OutputOverlay);

        Name(PropertyNames.BackdropColor, Strings.Backdrop);
        ui.SetPropertyControlType(PropertyNames.BackdropColor, PropertyControlType.ColorWheel);

        return ui;
    }

    protected override void OnCustomizeConfigUIWindowProperties(PropertyCollection props)
    {
        props[ControlInfoPropertyNames.WindowTitle]!.Value = Strings.WindowTitle;
        props[ControlInfoPropertyNames.WindowIsSizable]!.Value = true;
        base.OnCustomizeConfigUIWindowProperties(props);
    }

    protected override void OnSetToken(PropertyBasedEffectConfigToken? newToken)
    {
        if (newToken != null)
        {
            ColorBgra32 primary = Environment.PrimaryColor.GetBgra32(Environment.Document.ColorContext);
            Vector2Double pick = newToken.GetProperty<DoubleVectorProperty>(PropertyNames.PickPoint)!.Value;

            var next = new KeyerSettings
            {
                Mode = (KeyMode)newToken.GetProperty<StaticListChoiceProperty>(PropertyNames.Mode)!.Value,
                CustomKeyRgb = Int(newToken, PropertyNames.KeyColor),
                PrimaryRgb = (primary.R << 16) | (primary.G << 8) | primary.B,
                PickX = pick.X,
                PickY = pick.Y,
                Tolerance = Int(newToken, PropertyNames.Tolerance),
                Softness = Int(newToken, PropertyNames.Softness),
                LumaSensitivity = Int(newToken, PropertyNames.LumaSensitivity),
                UnevenCorrection = Bool(newToken, PropertyNames.UnevenCorrection),
                Enclosed = (EnclosedAreas)newToken.GetProperty<StaticListChoiceProperty>(PropertyNames.Enclosed)!.Value,
                SpeckSize = Int(newToken, PropertyNames.SpeckSize),
                EdgeDetail = Int(newToken, PropertyNames.EdgeDetail),
                RefineRadius = Int(newToken, PropertyNames.RefineRadius),
                ShrinkGrow = newToken.GetProperty<DoubleProperty>(PropertyNames.ShrinkGrow)!.Value,
                Feather = newToken.GetProperty<DoubleProperty>(PropertyNames.Feather)!.Value,
                ClipBlack = Int(newToken, PropertyNames.ClipBlack),
                ClipWhite = Int(newToken, PropertyNames.ClipWhite),
                Despill = Int(newToken, PropertyNames.Despill),
                Decontaminate = Int(newToken, PropertyNames.Decontaminate),
                Shadows = Int(newToken, PropertyNames.Shadows),
                Invert = Bool(newToken, PropertyNames.Invert),
                Output = (OutputMode)newToken.GetProperty<StaticListChoiceProperty>(PropertyNames.Output)!.Value,
                BackdropRgb = Int(newToken, PropertyNames.BackdropColor),
            };

            lock (sync)
            {
                if (next != settings || result == null)
                {
                    settings = next;
                    result = null;
                }
            }
        }

        base.OnSetToken(newToken);
    }

    private static int Int(PropertyBasedEffectConfigToken token, PropertyNames name) => token.GetProperty<Int32Property>(name)!.Value;

    private static bool Bool(PropertyBasedEffectConfigToken token, PropertyNames name) => token.GetProperty<BooleanProperty>(name)!.Value;

    protected override void OnInitializeRenderInfo(IBitmapEffectRenderInfo renderInfo)
    {
        renderInfo.OutputPixelFormat = PixelFormats.Bgra32;
        base.OnInitializeRenderInfo(renderInfo);
    }

    /// <summary>
    /// The matte depends on the whole image (background statistics, clean plate, connectivity), so it is computed
    /// once per setting change for the selection's bounding box; the tiles then just copy from it.
    /// </summary>
    private uint[]? EnsureResult(out RectInt32 bounds)
    {
        lock (sync)
        {
            bounds = region;
            if (result != null) return result;

            using IEffectInputBitmap<ColorBgra32> source = Environment.GetSourceBitmapBgra32();
            RectInt32 rect = RectInt32.Intersect(Environment.Selection.RenderBounds, new RectInt32(0, 0, source.Size));
            if (rect.Width <= 0 || rect.Height <= 0) return null;

            var pixels = new uint[rect.Width * rect.Height];
            using (IBitmapLock<ColorBgra32> srcLock = source.Lock(rect))
            {
                RegionPtr<ColorBgra32> src = srcLock.AsRegionPtr();
                for (int y = 0; y < rect.Height; y++)
                {
                    int row = y * rect.Width;
                    for (int x = 0; x < rect.Width; x++)
                    {
                        pixels[row + x] = src[x, y].Bgra;
                    }
                }
            }

            uint[]? computed = Keyer.Process(pixels, rect.Width, rect.Height, settings, CancellationToken);
            if (computed != null)
            {
                result = computed;
                region = rect;
                bounds = rect;
            }
            return computed;
        }
    }

    protected override void OnRender(IBitmapEffectOutput output)
    {
        if (IsCancelRequested) return;
        uint[]? keyed = EnsureResult(out RectInt32 bounds);
        if (keyed == null || IsCancelRequested) return;

        RectInt32 outRect = output.Bounds;
        using IBitmapLock<ColorBgra32> outLock = output.LockBgra32();
        RegionPtr<ColorBgra32> dst = outLock.AsRegionPtr();

        using IEffectInputBitmap<ColorBgra32> source = Environment.GetSourceBitmapBgra32();
        using IBitmapLock<ColorBgra32> srcLock = source.Lock(outRect);
        RegionPtr<ColorBgra32> src = srcLock.AsRegionPtr();

        for (int y = 0; y < outRect.Height; y++)
        {
            int iy = outRect.Top + y;
            bool rowInside = iy >= bounds.Top && iy < bounds.Bottom;
            for (int x = 0; x < outRect.Width; x++)
            {
                int ix = outRect.Left + x;
                if (rowInside && ix >= bounds.Left && ix < bounds.Right)
                {
                    dst[x, y] = ColorBgra32.FromUInt32(keyed[(iy - bounds.Top) * bounds.Width + (ix - bounds.Left)]);
                }
                else
                {
                    dst[x, y] = src[x, y];
                }
            }
        }
    }
}

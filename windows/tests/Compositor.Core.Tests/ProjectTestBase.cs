using System.Text;
using System.Text.Json;
using Compositor.Core.Format;
using Compositor.Core.IO;
using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Core.Tests;

/// <summary>A temporary folder, and the small pictures the project tests save and load.</summary>
public abstract class ProjectTestBase : IDisposable
{
    protected ProjectTestBase()
    {
        Root = Path.Combine(Path.GetTempPath(), "CompositorCoreTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    protected string Root { get; }

    protected string PathIn(string name) => Path.Combine(Root, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    protected static SKBitmap Picture(int width, int height, SKColor color)
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(width, height));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(color);
        return bitmap;
    }

    protected static SKBitmap Gray(int width, int height, byte value)
    {
        var bitmap = new SKBitmap(Bitmaps.MaskInfo(width, height));
        // Equal channels: Skia takes the luminance of the color it is given, so SKColor(v, 0, 0)
        // would land on 0.2126·v rather than v.
        bitmap.Erase(new SKColor(value, value, value));
        return bitmap;
    }

    /// <summary>
    /// A minimal PNG carrying 8-bit grayscale with alpha (color type 4). Skia will not author that
    /// format, and a mask may not be it, so the bytes are built by hand — the header is all the
    /// loader reads before it rejects one.
    /// </summary>
    protected static byte[] GrayWithAlphaPng(int width, int height)
    {
        var bytes = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        bytes[11] = 13;
        "IHDR"u8.CopyTo(bytes.AsSpan(12));
        bytes[16] = (byte)(width >> 24);
        bytes[17] = (byte)(width >> 16);
        bytes[18] = (byte)(width >> 8);
        bytes[19] = (byte)width;
        bytes[20] = (byte)(height >> 24);
        bytes[21] = (byte)(height >> 16);
        bytes[22] = (byte)(height >> 8);
        bytes[23] = (byte)height;
        bytes[24] = 8;
        bytes[25] = 4;
        return bytes;
    }

    protected static ImportedImage Asset(int width, int height, SKColor color, string name = "Layer") =>
        ImportedImage.Create(Picture(width, height, color), name);

    protected static ImportedImage MaskAsset(int width, int height, byte value = 255) =>
        ImportedImage.Create(Gray(width, height, value), "Layer Mask");

    /// <summary>Writes `manifest.json` and one PNG per image the manifest names.</summary>
    protected void WriteProject(string name, string manifestJson, params (string File, SKBitmap Image)[] assets)
    {
        var package = PathIn(name);
        Directory.CreateDirectory(Path.Combine(package, ProjectStore.ImagesName));
        foreach (var (file, image) in assets)
        {
            File.WriteAllBytes(Path.Combine(package, ProjectStore.ImagesName, file), PngCodec.Encode(image));
        }
        File.WriteAllBytes(Path.Combine(package, ProjectStore.ManifestName), Encoding.UTF8.GetBytes(manifestJson));
    }

    /// <summary>Every key any object in the document carries, in the order it was written.</summary>
    protected static List<string> KeyNames(string json)
    {
        var names = new List<string>();
        var document = JsonDocument.Parse(json);
        Walk(document.RootElement);
        return names;

        void Walk(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        names.Add(property.Name);
                        Walk(property.Value);
                    }
                    break;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray()) Walk(item);
                    break;
            }
        }
    }

    /// <summary>The raw text of one property at the top level of the manifest.</summary>
    protected static string Raw(string json, string property)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty(property, out var element) ? element.GetRawText() : "";
    }

    protected static string Serialized(ProjectManifest manifest) => Encoding.UTF8.GetString(ManifestJson.Serialize(manifest));

    protected static Format.LayerTransform Concrete(double x = 0, double y = 0, double width = 100, double height = 80) =>
        new() { Origin = new JsonPoint(x, y), Size = new JsonSize(width, height), Sampling = LayerSampling.HighQuality };

    protected static ProjectLayerRecord ImageLayer(Guid id, string name, string? imageFile = null) => new()
    {
        ID = id,
        Name = name,
        IsVisible = true,
        Transform = Concrete(),
        ImageFile = imageFile ?? Format.LayerMask.ExpectedImageFile(id),
    };

    protected static ProjectLayerRecord GroupLayer(Guid id, string name) => new()
    {
        ID = id,
        Name = name,
        IsVisible = true,
        Transform = Concrete(),
        IsGroup = true,
    };

    protected static ProjectManifest Manifest(int version, params ProjectLayerRecord[] layers) => new()
    {
        Version = version,
        DocumentID = Guid.NewGuid(),
        Width = 640,
        Height = 480,
        Resolution = 72,
        ActiveLayerID = layers.Length > 0 ? layers[0].ID : null,
        Layers = [.. layers],
    };

    protected void ExpectInvalid(ProjectManifest manifest) =>
        Assert.Equal(ProjectError.Invalid, Assert.Throws<ProjectException>(() => ProjectStore.Validate(manifest)).Error);

    protected void ExpectTooLarge(ProjectManifest manifest) =>
        Assert.Equal(ProjectError.TooLarge, Assert.Throws<ProjectException>(() => ProjectStore.Validate(manifest)).Error);

    protected static void ExpectValid(ProjectManifest manifest) => ProjectStore.Validate(manifest);

    // Every id carries hex letters, so a test can tell an uppercase uuid from a lowercase one.
    protected static readonly Guid BackgroundID = Guid.Parse("11111111-1111-4111-8111-1111111111A1");
    protected static readonly Guid ClippedID = Guid.Parse("22222222-2222-4222-8222-2222222222B2");
    protected static readonly Guid FolderID = Guid.Parse("33333333-3333-4333-8333-3333333333C3");
    protected static readonly Guid ChildID = Guid.Parse("44444444-4444-4444-8444-4444444444D4");
    protected static readonly Guid AdjustmentID = Guid.Parse("55555555-5555-4555-8555-5555555555E5");
    protected static readonly Guid DocumentID = Guid.Parse("99999999-9999-4999-8999-9999999999F6");

    /// <summary>
    /// One project holding every kind of record the format has: groups, masks, masks moved apart, a
    /// clipping link, an adjustment layer carrying all of its kinds, all six effects, editable text with
    /// per-letter faces and colors, a shape, and guides.
    /// </summary>
    protected ProjectSnapshot KitchenSink()
    {
        var manifest = new ProjectManifest
        {
            Version = 11,
            DocumentID = DocumentID,
            Width = 640,
            Height = 480,
            Resolution = 300,
            ActiveLayerID = ChildID,
            Layers =
            [
                new ProjectLayerRecord
                {
                    ID = BackgroundID,
                    Name = "Background",
                    IsVisible = true,
                    Transform = Concrete(0, 0, 640, 480),
                    ImageFile = Format.LayerMask.ExpectedImageFile(BackgroundID),
                    IsGroup = false,
                    Opacity = 0.9,
                    BlendMode = LayerBlendMode.Multiply,
                    Effects = new LayerEffects
                    {
                        Stroke = new StrokeEffect { Enabled = false, Size = 8, Red = 0.1, Green = 0.8, Blue = 0.2, Opacity = 0.9 },
                        Shadow = new ShadowEffect { Angle = 45, Distance = 15, Blur = 10, Red = 0.2, Green = 0.2, Blue = 0.3, Opacity = 0.75 },
                        ColorOverlay = new ColorOverlayEffect { Red = 0.9, Green = 0.1, Blue = 0.4, Opacity = 0.65 },
                        InnerShadow = new InnerShadowEffect { Angle = 135, Distance = 6, Blur = 4, Red = 0.05, Green = 0.05, Blue = 0.05, Opacity = 0.5 },
                        OuterGlow = new OuterGlowEffect { Size = 20, Red = 1, Green = 1, Blue = 0.5, Opacity = 0.75 },
                        InnerGlow = new InnerGlowEffect { Size = 12, Red = 0.5, Green = 1, Blue = 1, Opacity = 0.4 },
                    },
                },
                new ProjectLayerRecord
                {
                    ID = ClippedID,
                    Name = "Paint & sky 🌤",
                    IsVisible = true,
                    Transform = Concrete(-27.5, 88.25, 123, 47),
                    ImageFile = Format.LayerMask.ExpectedImageFile(ClippedID),
                    IsGroup = false,
                    Opacity = 1,
                    BlendMode = LayerBlendMode.ColorDodge,
                    MaskFile = Format.LayerMask.ExpectedFile(ClippedID),
                    MaskEnabled = false,
                    MaskPlacement = Concrete(10.5, 20.25, 60, 30),
                    MaskLinked = false,
                    MaskSourceID = BackgroundID,
                    Text = new LayerTextStyle
                    {
                        Content = "Hello 🌤 world",
                        FontName = "Helvetica-Bold",
                        FontSize = 48,
                        Red = 0.1,
                        Green = 0.2,
                        Blue = 0.3,
                        Alignment = TextAlignment.Center,
                        Tracking = 12.5,
                        Leading = 60,
                        BoxSize = new JsonSize(200, 100),
                        ColorRuns = [new LayerTextColorRun { Location = 0, Length = 5, Red = 1, Green = 0, Blue = 0 }],
                        FontRuns = [new LayerTextFontRun { Location = 6, Length = 5, FontName = "Menlo" }],
                    },
                },
                new ProjectLayerRecord
                {
                    ID = FolderID,
                    Name = "Folder 1",
                    IsVisible = true,
                    Transform = Concrete(0, 0, 640, 480),
                    IsGroup = true,
                    Opacity = 0.5,
                    BlendMode = LayerBlendMode.Normal,
                    MaskFile = Format.LayerMask.ExpectedFile(FolderID),
                    MaskEnabled = true,
                },
                new ProjectLayerRecord
                {
                    ID = ChildID,
                    Name = "Child",
                    IsVisible = false,
                    Transform = Concrete(30, 40, 5, 5),
                    ImageFile = Format.LayerMask.ExpectedImageFile(ChildID),
                    ParentID = FolderID,
                    IsGroup = false,
                    Opacity = 1,
                    BlendMode = LayerBlendMode.Luminosity,
                    Shape = new LayerShapeStyle
                    {
                        Kind = ShapeKind.Line,
                        Red = 0.2,
                        Green = 0.4,
                        Blue = 0.6,
                        CornerRadius = 0,
                        LineWidth = 3,
                        Start = new JsonPoint(0.1, 0.2),
                        End = new JsonPoint(0.9, 0.8),
                    },
                },
                new ProjectLayerRecord
                {
                    ID = AdjustmentID,
                    Name = "Warm Grade",
                    IsVisible = true,
                    Transform = Concrete(0, 0, 640, 480),
                    IsGroup = false,
                    Opacity = 1,
                    BlendMode = LayerBlendMode.Normal,
                    Adjustment = new LayerAdjustment
                    {
                        Kind = AdjustmentKind.Curves,
                        Hue = 15,
                        Saturation = -20,
                        Lightness = 5,
                        Colorize = false,
                        HsvSettings = BuildHsvSettings(),
                        Levels = new LevelsSettings
                        {
                            Channel = LevelsChannel.Green,
                            Ranges =
                            [
                                new LevelsChannelRange { Black = 10, Gamma = 1.2, White = 245, OutputBlack = 5, OutputWhite = 250 },
                                new LevelsChannelRange(),
                                new LevelsChannelRange(),
                                new LevelsChannelRange(),
                            ],
                        },
                        Curves = new CurvesSettings
                        {
                            Channel = LevelsChannel.Rgb,
                            Channels =
                            [
                                [new CurvePoint { X = 0, Y = 0 }, new CurvePoint { X = 255, Y = 255 }],
                                [new CurvePoint { X = 0, Y = 0 }, new CurvePoint { X = 120, Y = 147 }, new CurvePoint { X = 255, Y = 255 }],
                                [new CurvePoint { X = 0, Y = 0 }, new CurvePoint { X = 100, Y = 114 }, new CurvePoint { X = 255, Y = 255 }],
                                [new CurvePoint { X = 0, Y = 0 }, new CurvePoint { X = 115, Y = 97 }, new CurvePoint { X = 255, Y = 238 }],
                            ],
                        },
                        ExposureSettings = new ExposureSettings { Exposure = 1.5, Offset = -0.1, Gamma = 1.2 },
                        GradientMapSettings = new GradientMapSettings
                        {
                            Shadows = AdjustmentColor.From(0.1, 0.2, 0.3),
                            Highlights = AdjustmentColor.From(0.9, 0.8, 0.7),
                            Reversed = true,
                        },
                        GrainSettings = new GrainSettings { Amount = 30, Size = 2, Roughness = 40, Seed = 12345 },
                        BlackWhiteSettings = new BlackWhiteSettings { Reds = 20, Yellows = 70, Greens = 40, Cyans = 55, Blues = 25, Magentas = 85, Tint = true, TintHue = 45, TintSaturation = 25 },
                        ColorBalanceSettings = new ColorBalanceSettings
                        {
                            ShadowCyanRed = 10,
                            ShadowMagentaGreen = -10,
                            ShadowYellowBlue = 5,
                            MidCyanRed = -20,
                            MidMagentaGreen = 20,
                            MidYellowBlue = -5,
                            HighlightCyanRed = 30,
                            HighlightMagentaGreen = -30,
                            HighlightYellowBlue = 15,
                            PreserveLuminosity = false,
                        },
                        BlurRadius = 12.5,
                        MotionAngle = -30,
                        MotionDistance = 40,
                        NoiseAmount = 22,
                        NoiseGaussian = true,
                        NoiseMonochromatic = false,
                        NoiseSeed = 4_000_000_000,
                    },
                },
            ],
            Guides =
            [
                new CanvasGuide { ID = Guid.NewGuid(), Axis = GuideAxis.Horizontal, Position = 100.5 },
                new CanvasGuide { ID = Guid.NewGuid(), Axis = GuideAxis.Vertical, Position = -20.25 },
            ],
        };

        var snapshot = new ProjectSnapshot(manifest);
        snapshot.Images[BackgroundID] = Asset(12, 8, new SKColor(10, 20, 30, 200), "Background");
        snapshot.Images[ClippedID] = Asset(6, 4, new SKColor(200, 100, 5), "Paint & sky");
        snapshot.Images[ChildID] = Asset(5, 5, new SKColor(0, 255, 0), "Child");
        snapshot.Masks[ClippedID] = MaskAsset(6, 4, 128);
        snapshot.Masks[FolderID] = MaskAsset(8, 8, 255);
        return snapshot;
    }

    private static HueSaturationSettings BuildHsvSettings()
    {
        var settings = new HueSaturationSettings { Range = ColorRange.Reds, Colorize = false, InvertRange = true };
        settings.Set(12, -30, 4);
        settings.Adjustments.Set(ColorRange.Blues, new RangeAdjustment { Hue = -5, Saturation = 3, Lightness = -2 });
        settings.Bands.Set(ColorRange.Reds, new HueBand { FalloffStart = 300, RangeStart = 340, RangeEnd = 20, FalloffEnd = 60 });
        return settings;
    }
}

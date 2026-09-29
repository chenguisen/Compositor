using System.Text.Json.Serialization;

namespace Compositor.Core.Format;

/// <summary>The `manifest.json` of a `.comp` package.</summary>
public sealed class ProjectManifest
{
    /// <summary>The format version new saves write.</summary>
    public const int Current = 11;

    /// <summary>Every version <c>Load</c> accepts.</summary>
    public const int SupportedLower = 1;
    public const int SupportedUpper = Current;

    // Swift's synthesized Codable requires every non-optional property's key, default or not.
    [JsonRequired] public string Format { get; set; } = FormatIdentifier;
    [JsonRequired] public int Version { get; set; } = Current;
    [JsonRequired] public string ColorSpace { get; set; } = "sRGB";

    /// <summary>Older version-1 projects default to 72 pixels/inch.</summary>
    public double? Resolution { get; set; }

    [JsonRequired] public Guid DocumentID { get; set; }
    [JsonRequired] public int Width { get; set; }
    [JsonRequired] public int Height { get; set; }
    public Guid? ActiveLayerID { get; set; }

    /// <summary>Bottom to top.</summary>
    [JsonRequired] public List<ProjectLayerRecord> Layers { get; set; } = [];

    /// <summary>Alignment guides. Missing on versions 1–7.</summary>
    public List<CanvasGuide>? Guides { get; set; }

    public const string FormatIdentifier = "com.compositor.project";

    public static bool IsSupported(int version) => version is >= SupportedLower and <= SupportedUpper;
}

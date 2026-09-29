using System.Text.Json.Serialization;

namespace Compositor.Core.Format;

/// <summary>One layer, as the manifest stores it. Fields added by later versions are optional.</summary>
public sealed class ProjectLayerRecord
{
    [JsonRequired] public Guid ID { get; set; }
    [JsonRequired] public string Name { get; set; } = "";
    [JsonRequired] public bool IsVisible { get; set; }
    [JsonRequired] public LayerTransform Transform { get; set; } = new();
    public string? ImageFile { get; set; }
    public Guid? ParentID { get; set; }
    public bool? IsGroup { get; set; }
    public double? Opacity { get; set; }
    public LayerBlendMode? BlendMode { get; set; }
    public string? MaskFile { get; set; }
    public bool? MaskEnabled { get; set; }
    public Guid? MaskSourceID { get; set; }
    public LayerAdjustment? Adjustment { get; set; }

    /// <summary>A mask moved apart from its layer: where it sits on the document.</summary>
    public LayerTransform? MaskPlacement { get; set; }

    /// <summary>Nil (older projects) is linked.</summary>
    public bool? MaskLinked { get; set; }

    /// <summary>A shape layer's shape, drawn again when the layer is scaled.</summary>
    public LayerShapeStyle? Shape { get; set; }

    /// <summary>The stroke and drop shadow drawn around the layer.</summary>
    public LayerEffects? Effects { get; set; }

    public LayerTextStyle? Text { get; set; }

    /// <summary>A folder has no image file.</summary>
    public bool IsGroupValue => IsGroup ?? false;

    public double OpacityValue => Opacity ?? 1;

    public LayerBlendMode BlendModeValue => BlendMode ?? LayerBlendMode.Normal;
}

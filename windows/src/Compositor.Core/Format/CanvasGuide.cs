using System.Text.Json.Serialization;

namespace Compositor.Core.Format;

/// <summary>A user-placed alignment line. Horizontal guides sit at a document Y; vertical at a document X.</summary>
public sealed class CanvasGuide
{
    [JsonRequired] public Guid ID { get; set; }
    [JsonRequired] public GuideAxis Axis { get; set; }

    /// <summary>Document pixels: Y for a horizontal guide, X for a vertical one.</summary>
    [JsonRequired] public double Position { get; set; }
}

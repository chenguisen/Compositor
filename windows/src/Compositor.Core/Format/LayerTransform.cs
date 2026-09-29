using System.Text.Json.Serialization;

namespace Compositor.Core.Format;

/// <summary>Unrotated bounds in document pixels; rotation is clockwise around their center.</summary>
public sealed class LayerTransform
{
    [JsonRequired] public JsonPoint Origin { get; set; }
    [JsonRequired] public JsonSize Size { get; set; }
    [JsonRequired] public double Rotation { get; set; }
    [JsonRequired] public bool FlipX { get; set; }
    [JsonRequired] public bool FlipY { get; set; }
    [JsonRequired] public LayerSampling Sampling { get; set; }

    public Model.LayerTransform ToRuntime() => new(Origin.X, Origin.Y, Size.Width, Size.Height, Rotation, FlipX, FlipY, Sampling);

    public static LayerTransform FromRuntime(Model.LayerTransform transform) => new()
    {
        Origin = new JsonPoint(transform.X, transform.Y),
        Size = new JsonSize(transform.Width, transform.Height),
        Rotation = transform.Rotation,
        FlipX = transform.FlipX,
        FlipY = transform.FlipY,
        Sampling = transform.Sampling,
    };
}

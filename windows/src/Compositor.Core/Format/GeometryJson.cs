using System.Text.Json;
using System.Text.Json.Serialization;

namespace Compositor.Core.Format;

/// <summary>A CGPoint on the wire: JSON <c>[x, y]</c>, the way Foundation encodes it.</summary>
public readonly record struct JsonPoint(double X, double Y)
{
    public static readonly JsonPoint Zero = new(0, 0);
}

/// <summary>A CGSize on the wire: JSON <c>[width, height]</c>.</summary>
public readonly record struct JsonSize(double Width, double Height)
{
    public static readonly JsonSize Zero = new(0, 0);
}

public sealed class JsonPointConverter : JsonConverter<JsonPoint>
{
    public override JsonPoint Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var values = JsonArrayReader.ReadNumbers(ref reader, "point");
        return new JsonPoint(values[0], values[1]);
    }

    public override void Write(Utf8JsonWriter writer, JsonPoint value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(value.X);
        writer.WriteNumberValue(value.Y);
        writer.WriteEndArray();
    }
}

public sealed class JsonSizeConverter : JsonConverter<JsonSize>
{
    public override JsonSize Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var values = JsonArrayReader.ReadNumbers(ref reader, "size");
        return new JsonSize(values[0], values[1]);
    }

    public override void Write(Utf8JsonWriter writer, JsonSize value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(value.Width);
        writer.WriteNumberValue(value.Height);
        writer.WriteEndArray();
    }
}

internal static class JsonArrayReader
{
    /// <summary>The first two numbers of an array. Extra entries are ignored, as Foundation's decoders do.</summary>
    public static double[] ReadNumbers(ref Utf8JsonReader reader, string what)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var element = document.RootElement;
        if (element.ValueKind != JsonValueKind.Array) throw new JsonException($"A {what} must be an array of two numbers.");
        if (element.GetArrayLength() < 2) throw new JsonException($"A {what} must hold two numbers.");
        return [element[0].GetDouble(), element[1].GetDouble()];
    }
}

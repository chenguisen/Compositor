using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Compositor.Core.Format;

/// <summary>Formats a UUID the way Swift's <c>UUID.uuidString</c> does: uppercase, with dashes.</summary>
public sealed class UpperCaseGuidConverter : JsonConverter<Guid>
{
    public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("A UUID must be a string.");
        var text = reader.GetString();
        return Guid.TryParse(text, out var value) ? value : throw new JsonException($"'{text}' is not a UUID.");
    }

    public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString("D").ToUpperInvariant());
}

/// <summary>
/// A Swift <c>[Key: Value]</c> dictionary whose key is a string-raw enum. Swift only writes these as JSON
/// objects when the key type conforms to CodingKeyRepresentable, which its ColorRange does not, so the
/// pairs are written into an array: <c>["Master", { … }, "Reds", { … }]</c>. Iteration order is hash order
/// in Swift and therefore not stable; this writes the enum's declaration order instead, which is one of the
/// orders Swift itself can produce.
/// </summary>
public sealed class EnumKeyedValues<TKey, TValue> where TKey : struct, Enum
{
    public List<KeyValuePair<TKey, TValue>> Entries { get; } = [];

    public IEnumerable<TValue> Values => Entries.Select(entry => entry.Value);

    public TValue? Find(TKey key)
    {
        foreach (var entry in Entries)
        {
            if (EqualityComparer<TKey>.Default.Equals(entry.Key, key)) return entry.Value;
        }
        return default;
    }

    public void Set(TKey key, TValue value)
    {
        for (var i = 0; i < Entries.Count; i++)
        {
            if (EqualityComparer<TKey>.Default.Equals(Entries[i].Key, key))
            {
                Entries[i] = new KeyValuePair<TKey, TValue>(key, value);
                return;
            }
        }
        Entries.Add(new KeyValuePair<TKey, TValue>(key, value));
    }
}

public sealed class EnumKeyedValuesConverter<TKey, TValue> : JsonConverter<EnumKeyedValues<TKey, TValue>>
    where TKey : struct, Enum
{
    public override EnumKeyedValues<TKey, TValue> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) throw new JsonException("A dictionary may not be null here.");
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("A dictionary must be an array of alternating keys and values.");
        var values = new EnumKeyedValues<TKey, TValue>();
        while (true)
        {
            if (!reader.Read()) throw new JsonException("The dictionary ended early.");
            if (reader.TokenType == JsonTokenType.EndArray) break;
            var key = JsonSerializer.Deserialize<TKey>(ref reader, options);
            if (!reader.Read()) throw new JsonException("The dictionary ended without a value.");
            if (reader.TokenType == JsonTokenType.EndArray) throw new JsonException("A dictionary key has no value.");
            var value = JsonSerializer.Deserialize<TValue>(ref reader, options);
            if (value is not null) values.Set(key, value);
        }
        return values;
    }

    public override void Write(Utf8JsonWriter writer, EnumKeyedValues<TKey, TValue> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var entry in value.Entries)
        {
            JsonSerializer.Serialize(writer, entry.Key, options);
            JsonSerializer.Serialize(writer, entry.Value, options);
        }
        writer.WriteEndArray();
    }
}

/// <summary>Reading and writing the manifest, matching Swift's JSONEncoder settings and key names.</summary>
public static class ManifestJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            // Swift's synthesized Codable requires every non-optional property, whatever its default.
            RespectNullableAnnotations = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            // Swift escapes only what JSON needs, so non-ASCII names stay readable.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.Converters.Add(new UpperCaseGuidConverter());
        options.Converters.Add(new JsonPointConverter());
        options.Converters.Add(new JsonSizeConverter());
        options.Converters.Add(new JsonStringEnumConverter(null, allowIntegerValues: false));
        // The file carries data only. The records expose computed conveniences next to it
        // (`IsGroupValue`, `ResolvedNoiseSeed`, `LineHeight`); reflection would write them all, so a
        // property without a setter is taken to be a derivation and left out of the format.
        options.TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { IgnoreComputedProperties } };
        return options;
    }

    private static void IgnoreComputedProperties(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object) return;
        for (var index = typeInfo.Properties.Count - 1; index >= 0; index--)
        {
            if (typeInfo.Properties[index].Set is null) typeInfo.Properties.RemoveAt(index);
        }
    }

    /// <summary>
    /// The manifest as Swift writes it: pretty-printed with every object's keys sorted, so files are
    /// diffable and re-saves are stable. Foundation separates a pretty-printed key from its value with
    /// <c>:</c> spaces on both sides; this writes the ordinary <c>key: value</c>, which parses the same.
    /// </summary>
    public static byte[] Serialize(ProjectManifest manifest)
    {
        var node = JsonSerializer.SerializeToNode(manifest, Options)
                   ?? throw new JsonException("The manifest could not be written.");
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = true,
            IndentCharacter = ' ',
            IndentSize = 2,
            // Foundation ends a pretty-printed line with a bare newline; the platform default
            // (CRLF on Windows) would make every save a diff against a file the Mac wrote.
            NewLine = "\n",
            Encoder = Options.Encoder,
        }))
        {
            WriteSorted(writer, node);
        }
        return buffer.WrittenSpan.ToArray();
    }

    public static ProjectManifest Deserialize(ReadOnlySpan<byte> json)
    {
        var manifest = JsonSerializer.Deserialize<ProjectManifest>(json, Options);
        return manifest ?? throw new JsonException("The manifest is empty.");
    }

    /// <summary>The first two fields of a manifest, which is all the version check needs.</summary>
    public readonly record struct Header(string? Format, int Version);

    public static Header ReadHeader(ReadOnlyMemory<byte> json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("A manifest must be an object.");
        if (!root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String) throw new JsonException("A manifest needs a format.");
        if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number) throw new JsonException("A manifest needs a version.");
        return new Header(format.GetString(), version.GetInt32());
    }

    private static void WriteSorted(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonObject obj:
                writer.WriteStartObject();
                foreach (var property in obj.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Key);
                    WriteSorted(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonArray array:
                writer.WriteStartArray();
                foreach (var item in array) WriteSorted(writer, item);
                writer.WriteEndArray();
                break;
            default:
                node.WriteTo(writer);
                break;
        }
    }
}

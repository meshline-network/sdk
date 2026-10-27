using System.Buffers.Text;
using System.Text.Json;

namespace Meshline.Tests.Support;

internal static class ProtocolVectors
{
    internal static JsonElement Read(string file)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "Vectors", file + "-v1.json")));
        return document.RootElement.Clone();
    }

    internal static byte[] Decode(string text) => Base64Url.DecodeFromChars(text);

    internal static JsonElement Resolve(JsonElement root, JsonElement record, string name)
    {
        if (record.TryGetProperty(name, out var value)) return value;
        var path = record.GetProperty(name + "_ref").GetString()!;
        Assert.StartsWith("/", path, StringComparison.Ordinal);
        value = root;
        foreach (var segment in path[1..].Split('/'))
            value = value.ValueKind == JsonValueKind.Array
                ? value[int.Parse(segment, System.Globalization.CultureInfo.InvariantCulture)]
                : value.GetProperty(segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal));
        return value;
    }
}

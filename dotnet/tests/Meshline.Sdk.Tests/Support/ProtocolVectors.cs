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
}

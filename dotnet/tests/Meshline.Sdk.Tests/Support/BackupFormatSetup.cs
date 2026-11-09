using Meshline.Backups;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Meshline.Tests.Support;

internal static class BackupFormatSetup
{
    internal static async Task<JsonDocument> ReadVectorAsync(CancellationToken token) => JsonDocument.Parse(
        await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "TestData", "Vectors", "backup-v1.json"), token));

    internal static async Task<byte[]> RewriteRecordsAsync(byte[] bytes, string password, CancellationToken token, Func<string, JsonElement, JsonElement> transform)
    {
        using var input = new MemoryStream(bytes);
        using var output = new MemoryStream();
        await using var reader = await BackupCodec.OpenAsync(input, password, token);
        await using var writer = await BackupCodec.CreateAsync(output, password, token);
        while (true)
        {
            var frame = await reader.ReadAsync(token);
            var type = frame.GetProperty("type").GetString()!;
            await writer.WriteAsync(type, transform(type, frame.GetProperty("data")), token);
            if (type == "end")
            {
                await reader.RequireEndAsync(token);
                await writer.CompleteAsync(token);
                return output.ToArray();
            }
        }
    }

    internal static byte[] Records(params string[] records)
    {
        using var stream = new MemoryStream();
        foreach (var record in records)
        {
            var bytes = Encoding.UTF8.GetBytes(record);
            var prefix = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(prefix, bytes.Length);
            stream.Write(prefix);
            stream.Write(bytes);
        }
        return stream.ToArray();
    }

    internal static async Task<byte[]> CompressAsync(byte[] plaintext, CancellationToken token)
    {
        // The framework can emit no bytes for an unused writer. This is a complete empty RFC 1952 member.
        if (plaintext.Length == 0)
            return Convert.FromHexString("1f8b08000000000000ff03000000000000000000");
        using var output = new MemoryStream();
        await using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            await gzip.WriteAsync(plaintext, token);
        return output.ToArray();
    }

    internal static byte[] Preamble(string header)
    {
        var headerBytes = Encoding.UTF8.GetBytes(header);
        var preamble = new byte[12 + headerBytes.Length];
        "MLBK0001"u8.CopyTo(preamble);
        BinaryPrimitives.WriteInt32BigEndian(preamble.AsSpan(8, 4), headerBytes.Length);
        headerBytes.CopyTo(preamble, 12);
        return preamble;
    }

    internal static async Task<byte[]> EncryptAsync(byte[] compressed, string password, CancellationToken token, BackupPassword.Parameters? derivation = null)
    {
        derivation ??= BackupPassword.CreateParameters();
        var key = await BackupPassword.DeriveAsync(password, derivation, token);
        try
        {
            var preamble = Preamble(JsonSerializer.Serialize(new BackupCodec.Header(derivation), BackupCodec.Json));
            using var output = new MemoryStream();
            await output.WriteAsync(preamble, token);
            using var encryption = await BackupEncryptionStream.CreateAsync(output, key, preamble, token);
            await encryption.WriteAsync(compressed, token);
            await encryption.CompleteAsync(token);
            return output.ToArray();
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    internal static async Task<byte[]> DecryptAsync(byte[] backup, string password, CancellationToken token)
    {
        using var input = new MemoryStream(backup);
        var header = await BackupCodec.ReadHeaderAsync(input, token);
        var key = await BackupPassword.DeriveAsync(password, header.Header.KeyDerivation, token);
        try
        {
            using var encryption = await BackupEncryptionStream.OpenAsync(input, key, header.Preamble, token);
            using var output = new MemoryStream();
            await encryption.CopyToAsync(output, token);
            return output.ToArray();
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
}

internal sealed class BackupShortReadStream(byte[] bytes, int maximumRead = 137) : MemoryStream(bytes)
{
    public override bool CanSeek => false;
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        base.ReadAsync(buffer[..Math.Min(buffer.Length, maximumRead)], cancellationToken);
}

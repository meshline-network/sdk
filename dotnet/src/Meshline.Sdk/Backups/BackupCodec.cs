using Meshline.Serialization;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meshline.Backups;

// This is an SDK file format, not a network protocol object.
internal sealed class BackupCodec : IAsyncDisposable
{
    internal const int MaximumRecord = 4_194_304;
    static ReadOnlySpan<byte> Magic => "MLBK0001"u8;
    internal static readonly JsonSerializerOptions Json = new()
    {
        Encoder = CanonicalJsonEncoder.Instance,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 32,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) }
    };
    readonly Stream _stream;
    readonly BackupEncryptionStream _encryption;
    readonly GZipStream _compression;

    BackupCodec(Stream stream, BackupEncryptionStream encryption, GZipStream compression)
    {
        _stream = stream;
        _encryption = encryption;
        _compression = compression;
    }

    internal sealed record Header(BackupPassword.Parameters KeyDerivation);

    internal static async Task<BackupCodec> CreateAsync(Stream stream, string password, CancellationToken token)
    {
        var derivation = BackupPassword.CreateParameters();
        var key = await BackupPassword.DeriveAsync(password, derivation, token).ConfigureAwait(false);
        BackupEncryptionStream encryption;
        try
        {
            var preamble = Preamble(JsonSerializer.SerializeToUtf8Bytes(new Header(derivation), Json));
            await stream.WriteAsync(preamble, token).ConfigureAwait(false);
            encryption = await BackupEncryptionStream.CreateAsync(stream, key, preamble, token).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
        try
        {
            return new(stream, encryption, new GZipStream(encryption, CompressionLevel.Optimal, leaveOpen: true));
        }
        catch { encryption.Dispose(); throw; }
    }

    static byte[] Preamble(byte[] header)
    {
        var bytes = new byte[12 + header.Length];
        Magic.CopyTo(bytes);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8, 4), header.Length);
        header.CopyTo(bytes, 12);
        return bytes;
    }

    internal static async Task<(Header Header, byte[] Preamble)> ReadHeaderAsync(Stream stream, CancellationToken token)
    {
        var magic = new byte[8];
        await stream.ReadExactlyAsync(magic, token).ConfigureAwait(false);
        if (!magic.AsSpan().SequenceEqual(Magic))
            throw new InvalidDataException("Unsupported SDK backup format or version.");
        var length = await ReadLengthAsync(stream, 65536, token).ConfigureAwait(false);
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        using var document = ParseJson(bytes);
        var header = document.RootElement.Deserialize<Header>(Json) ?? throw new InvalidDataException("Missing backup header.");
        return (header, Preamble(bytes));
    }

    internal static async Task<BackupCodec> OpenAsync(Stream stream, string password, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        var header = await ReadHeaderAsync(stream, token).ConfigureAwait(false);
        var key = await BackupPassword.DeriveAsync(password, header.Header.KeyDerivation, token).ConfigureAwait(false);
        BackupEncryptionStream encryption;
        try
        {
            encryption = await BackupEncryptionStream.OpenAsync(stream, key, header.Preamble, token).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
        try
        {
            return new(stream, encryption, new GZipStream(encryption, CompressionMode.Decompress, leaveOpen: true));
        }
        catch { encryption.Dispose(); throw; }
    }

    internal Task WriteAsync(string type, object data, CancellationToken token) => WriteRecordAsync(new { type, data }, token);

    internal Task WriteAsync(JsonElement record, CancellationToken token) => WriteRecordAsync(record, token);

    async Task WriteRecordAsync(object record, CancellationToken token)
    {
        var plaintext = EncodeRecord(record);
        try
        {
            await WriteLengthAsync(_compression, plaintext.Length, token).ConfigureAwait(false);
            await _compression.WriteAsync(plaintext, token).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    internal static void ValidateRecordSize(string type, object data) => CryptographicOperations.ZeroMemory(EncodeRecord(new { type, data }));

    internal static void ValidateRecord(JsonElement record)
    {
        var bytes = EncodeRecord(record);
        try
        {
            using var document = ParseJson(bytes);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    static byte[] EncodeRecord(object record)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, Json);
        if (bytes.Length <= MaximumRecord)
            return bytes;
        CryptographicOperations.ZeroMemory(bytes);
        throw new InvalidDataException("A backup record exceeds 4 MiB.");
    }

    internal async Task<JsonElement> ReadAsync(CancellationToken token)
    {
        var length = await ReadLengthAsync(_compression, MaximumRecord, token).ConfigureAwait(false);
        var plaintext = new byte[length];
        try
        {
            await _compression.ReadExactlyAsync(plaintext, token).ConfigureAwait(false);
            using var document = ParseJson(plaintext);
            BackupData.ReadRecord(document.RootElement);
            return document.RootElement.Clone();
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    internal async Task CompleteAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await _compression.DisposeAsync().ConfigureAwait(false);
        await _encryption.CompleteAsync(token).ConfigureAwait(false);
        await _stream.FlushAsync(token).ConfigureAwait(false);
    }

    internal async Task RequireEndAsync(CancellationToken token)
    {
        var extra = new byte[1];
        if (await _compression.ReadAsync(extra, token).ConfigureAwait(false) != 0)
            throw new InvalidDataException("Data follows the authenticated backup end marker.");
        // A gzip decoder may stop before consuming all authenticated compressed bytes.
        await _encryption.CopyToAsync(Stream.Null, 65536, token).ConfigureAwait(false);
        if (await _stream.ReadAsync(extra, token).ConfigureAwait(false) != 0)
            throw new InvalidDataException("Data follows the backup stream.");
    }

    static async Task<int> ReadLengthAsync(Stream stream, int maximum, CancellationToken token)
    {
        var bytes = new byte[4];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32BigEndian(bytes);
        if (length <= 0 || length > maximum)
            throw new InvalidDataException("Invalid backup record length.");
        return length;
    }

    static async Task WriteLengthAsync(Stream stream, int length, CancellationToken token)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, length);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
    }

    static JsonDocument ParseJson(byte[] bytes)
    {
        var document = JsonDocument.Parse(bytes, new()
        {
            MaxDepth = Json.MaxDepth
        });
        try
        {
            Visit(document.RootElement);
            return document;
        }
        catch { document.Dispose(); throw; }

        static void Visit(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                        throw new InvalidDataException("Duplicate backup JSON property.");
                    Visit(property.Value);
                }
            }
            else if (value.ValueKind == JsonValueKind.Array)
                foreach (var element in value.EnumerateArray())
                    Visit(element);
            else if (value.ValueKind == JsonValueKind.String)
                _ = value.GetString();
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Abort encryption first: cleanup of an unfinished gzip writer must not
        // write to the destination or complete an abandoned encrypted backup.
        _encryption.Dispose();
        try
        {
            await _compression.DisposeAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException) { } // The gzip writer attempted to finish into the aborted stream.
    }
}

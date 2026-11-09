using Meshline.Backups;
using Meshline.Tests.Support;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Meshline.Tests.Backups;

public sealed class BackupFormatTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;
    const string Password = "format test password 🔐";

    [Fact]
    public async Task Reads_independent_fixed_vector_including_exact_non_ascii_plaintext()
    {
        using var json = await BackupFormatSetup.ReadVectorAsync(Token);
        var vector = json.RootElement;
        var bytes = Convert.FromBase64String(vector.GetProperty("backup_base64").GetString()!);
        var password = vector.GetProperty("password").GetString()!;
        Assert.Equal(vector.GetProperty("backup_sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(bytes)));
        using var input = new MemoryStream(bytes);
        await using var codec = await BackupCodec.OpenAsync(input, password, Token);
        foreach (var text in vector.GetProperty("records_utf8").EnumerateArray())
        {
            using var expected = JsonDocument.Parse(text.GetString()!);
            var frame = await codec.ReadAsync(Token);
            Assert.Equal(expected.RootElement.GetProperty("type").GetString(), frame.GetProperty("type").GetString());
            Assert.Equal(expected.RootElement.GetProperty("data").GetRawText(), frame.GetProperty("data").GetRawText());
        }
        await codec.RequireEndAsync(Token);
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)248)] // The Base64 salt starts with '+'.
    public async Task Rejects_changed_public_header_with_valid_salt(byte saltPrefix)
    {
        var salt = new byte[16];
        salt[0] = saltPrefix;
        var derivation = BackupPassword.CreateParameters() with
        {
            Salt = Convert.ToBase64String(salt)
        };
        var plaintext = BackupFormatSetup.Records("{\"type\":\"manifest\",\"data\":{}}");
        var bytes = await BackupFormatSetup.EncryptAsync(await BackupFormatSetup.CompressAsync(plaintext, Token), Password, Token, derivation);

        var headerLength = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(8));
        var header = JsonSerializer.Deserialize<BackupCodec.Header>(bytes.AsSpan(12, headerLength), BackupCodec.Json)!;
        var changedSalt = Convert.FromBase64String(header.KeyDerivation.Salt);
        changedSalt[0] ^= 1;
        var changedHeader = header with
        {
            KeyDerivation = header.KeyDerivation with
            {
                Salt = Convert.ToBase64String(changedSalt)
            }
        };
        var preamble = BackupFormatSetup.Preamble(JsonSerializer.Serialize(changedHeader, BackupCodec.Json));
        using var input = new MemoryStream([.. preamble, .. bytes.AsSpan(12 + headerLength)]);
        await using var reader = await BackupCodec.OpenAsync(input, Password, Token);
        await Assert.ThrowsAsync<AuthenticationTagMismatchException>(() => reader.ReadAsync(Token));
    }

    [Fact]
    public async Task Rejects_duplicate_record_properties()
    {
        using var output = new MemoryStream();
        await using (var codec = await BackupCodec.CreateAsync(output, Password, Token))
        {
            using var data = JsonDocument.Parse("{\"a\":1,\"a\":2}");
            await codec.WriteAsync("manifest", data.RootElement, Token);
            await codec.CompleteAsync(Token);
        }
        using var input = new MemoryStream(output.ToArray());
        await using var reader = await BackupCodec.OpenAsync(input, Password, Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadAsync(Token));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4_194_305)]
    public async Task Rejects_invalid_record_lengths_inside_authenticated_compressed_data(int length)
    {
        var plaintext = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(plaintext, length);
        var bytes = await BackupFormatSetup.EncryptAsync(await BackupFormatSetup.CompressAsync(plaintext, Token), Password, Token);
        using var input = new MemoryStream(bytes);
        await using var reader = await BackupCodec.OpenAsync(input, Password, Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadAsync(Token));
    }

    [Theory]
    [InlineData("{\"key_derivation\":null,\"key_derivation\":null}")]
    [InlineData("{\"unknown\":1}")]
    public async Task Rejects_duplicate_and_unknown_public_header_fields(string header)
    {
        using var input = new MemoryStream(BackupFormatSetup.Preamble(header));
        await Assert.ThrowsAnyAsync<Exception>(() => BackupCodec.OpenAsync(input, Password, Token));
    }

    [Theory]
    [InlineData((byte)'0')]
    [InlineData((byte)'2')]
    public async Task Rejects_unknown_container_versions(byte version)
    {
        var bytes = BackupFormatSetup.Preamble("{}");
        bytes[7] = version;
        using var input = new MemoryStream(bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => BackupCodec.OpenAsync(input, Password, Token));
    }

    [Fact]
    public async Task Password_import_starts_at_the_current_position_without_seeking()
    {
        using var output = new MemoryStream();
        await output.WriteAsync(new byte[7], Token);
        await using (var writer = await BackupCodec.CreateAsync(output, Password, Token))
        {
            await writer.WriteAsync("end", new
            {
                records = 0
            }, Token);
            await writer.CompleteAsync(Token);
        }
        using var input = new BackupShortReadStream(output.ToArray());
        await input.ReadExactlyAsync(new byte[7], Token);
        await using var reader = await BackupCodec.OpenAsync(input, Password, Token);
        Assert.Equal("end", (await reader.ReadAsync(Token)).GetProperty("type").GetString());
        await reader.RequireEndAsync(Token);
    }

    [Fact]
    public async Task Records_cross_encryption_segments_with_bounded_nonseekable_reads()
    {
        const long firstIndex = 9007199254740993L;
        var random = new Random(1729);
        var values = new List<string>();
        using var output = new MemoryStream();
        await using (var writer = await BackupCodec.CreateAsync(output, Password, Token))
        {
            for (var i = 0; i < 6; i++)
            {
                var payload = new byte[500_000];
                random.NextBytes(payload);
                var value = Convert.ToBase64String(payload);
                values.Add(value);
                await writer.WriteAsync("message", new
                {
                    index = firstIndex + i,
                    value
                }, Token);
            }
            await writer.WriteAsync("end", new
            {
                records = values.Count
            }, Token);
            await writer.CompleteAsync(Token);
        }
        Assert.True(output.Length > BackupEncryptionStream.SegmentSize * 2);
        using var input = new BackupShortReadStream(output.ToArray(), 65_537);
        await using var reader = await BackupCodec.OpenAsync(input, Password, Token);
        for (var i = 0; i < values.Count; i++)
        {
            var record = await reader.ReadAsync(Token);
            Assert.Equal("message", record.GetProperty("type").GetString());
            Assert.Equal(firstIndex + i, record.GetProperty("data").GetProperty("index").GetInt64());
            Assert.Equal(values[i], record.GetProperty("data").GetProperty("value").GetString());
        }
        Assert.Equal("end", (await reader.ReadAsync(Token)).GetProperty("type").GetString());
        await reader.RequireEndAsync(Token);
    }

    [Fact]
    public async Task Continuous_compression_reuses_content_across_records()
    {
        var payload = new byte[5000];
        new Random(1729).NextBytes(payload);
        var value = Convert.ToBase64String(payload);
        using var output = new MemoryStream();
        await using (var writer = await BackupCodec.CreateAsync(output, Password, Token))
        {
            for (var i = 0; i < 100; i++)
                await writer.WriteAsync("message", new
                {
                    index = i,
                    value
                }, Token);
            await writer.CompleteAsync(Token);
        }
        Assert.True(output.Length < Encoding.UTF8.GetByteCount(value) * 10);
    }

    [Fact]
    public async Task A_four_MiB_record_roundtrips_across_encryption_segments()
    {
        const int limit = 4_194_304;
        var overhead = JsonSerializer.SerializeToUtf8Bytes(BackupData.Record("message", new
        {
            text = ""
        }), BackupCodec.Json).Length;
        var textLength = limit - overhead;
        var randomBytes = new byte[textLength / 4 * 3];
        new Random(1729).NextBytes(randomBytes);
        var text = Convert.ToBase64String(randomBytes) + new string('x', textLength % 4);
        var record = BackupData.Record("message", new
        {
            text
        });
        Assert.Equal(limit, JsonSerializer.SerializeToUtf8Bytes(record, BackupCodec.Json).Length);
        using var output = new MemoryStream();
        await using (var writer = await BackupCodec.CreateAsync(output, Password, Token))
        {
            await writer.WriteAsync(record, Token);
            await writer.WriteAsync("end", new
            {
                records = 1
            }, Token);
            await writer.CompleteAsync(Token);
        }
        Assert.True(output.Length > 2 * BackupEncryptionStream.SegmentSize);
        using var input = new BackupShortReadStream(output.ToArray(), 65_537);
        await using var reader = await BackupCodec.OpenAsync(input, Password, Token);
        Assert.Equal(text, (await reader.ReadAsync(Token)).GetProperty("data").GetProperty("text").GetString());
        Assert.Equal("end", (await reader.ReadAsync(Token)).GetProperty("type").GetString());
        await reader.RequireEndAsync(Token);
    }

    [Fact]
    public async Task Rejects_a_record_exceeding_the_limit_before_completing_the_export()
    {
        using var output = new MemoryStream();
        await using var writer = await BackupCodec.CreateAsync(output, Password, Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => writer.WriteAsync("message", new { text = new string('x', BackupCodec.MaximumRecord) }, Token));
    }

    [Fact]
    public async Task End_validation_rejects_extra_logical_records()
    {
        using var output = new MemoryStream();
        await using (var writer = await BackupCodec.CreateAsync(output, Password, Token))
        {
            await writer.WriteAsync("end", new
            {
                records = 0
            }, Token);
            await writer.WriteAsync("message", new
            {
            }, Token);
            await writer.CompleteAsync(Token);
        }
        output.Position = 0;
        await using var reader = await BackupCodec.OpenAsync(output, Password, Token);
        Assert.Equal("end", (await reader.ReadAsync(Token)).GetProperty("type").GetString());
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.RequireEndAsync(Token));
    }
}

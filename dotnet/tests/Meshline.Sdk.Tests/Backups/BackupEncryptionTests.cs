using Meshline.Backups;
using Meshline.Tests.Support;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Meshline.Tests.Backups;

public sealed class BackupEncryptionTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;
    static readonly byte[] Key = SHA256.HashData("streaming-test-key"u8);
    static readonly byte[] Aad = "bound public header"u8.ToArray();

    [Fact]
    public async Task Decrypts_official_Tink_vectors_including_nonfinal_and_final_segments()
    {
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "TestData", "Vectors", "tink-streaming-aead.json"), Token));
        var root = json.RootElement;
        var key = Convert.FromHexString(root.GetProperty("key_hex").GetString()!);
        var aad = Convert.FromHexString(root.GetProperty("associated_data_hex").GetString()!);
        foreach (var vector in root.GetProperty("cases").EnumerateArray())
        {
            var ciphertext = Convert.FromBase64String(vector.GetProperty("ciphertext_base64").GetString()!);
            Assert.Equal(vector.GetProperty("ciphertext_sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(ciphertext)));
            using var input = new BackupShortReadStream(ciphertext);
            using var reader = await BackupEncryptionStream.OpenAsync(input, key, aad, Token);
            using var output = new MemoryStream();
            await reader.CopyToAsync(output, Token);
            var plaintext = output.ToArray();
            Assert.Equal(vector.GetProperty("plaintext_length").GetInt32(), plaintext.Length);
            Assert.Equal(vector.GetProperty("plaintext_sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(plaintext)));
            if (vector.TryGetProperty("plaintext_utf8", out var text))
                Assert.Equal(Encoding.UTF8.GetBytes(text.GetString()!), plaintext);
            else
                for (var i = 0; i < plaintext.Length; i++)
                    Assert.Equal((byte)(i % 251), plaintext[i]);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1048519)]
    [InlineData(1048520)]
    [InlineData(1048521)]
    [InlineData(2097079)]
    [InlineData(2097080)]
    [InlineData(2097081)]
    [InlineData(3145640)]
    [InlineData(3145641)]
    public async Task Variable_writes_and_nonseekable_short_reads_preserve_exact_segment_boundaries(int length)
    {
        var plaintext = new byte[length];
        new Random(1729).NextBytes(plaintext);
        using var output = new MemoryStream();
        using (var writer = await BackupEncryptionStream.CreateAsync(output, Key, Aad, Token))
        {
            for (var offset = 0; offset < plaintext.Length;)
            {
                var count = Math.Min(65_537, plaintext.Length - offset);
                await writer.WriteAsync(plaintext.AsMemory(offset, count), Token);
                await writer.FlushAsync(Token);
                offset += count;
            }
            await writer.CompleteAsync(Token);
        }
        var segments = length <= 1048520 ? 1 : 1 + ((long)length - 1048520 + 1048559) / 1048560;
        Assert.Equal(40 + length + 16 * segments, output.Length);
        Assert.True(output.CanWrite);
        using var input = new BackupShortReadStream(output.ToArray());
        using var reader = await BackupEncryptionStream.OpenAsync(input, Key, Aad, Token);
        using var recovered = new MemoryStream();
        await reader.CopyToAsync(recovered, 997, Token);
        Assert.Equal(plaintext, recovered.ToArray());
    }

    [Theory]
    [InlineData("key")]
    [InlineData("aad")]
    [InlineData("salt")]
    [InlineData("nonce")]
    [InlineData("tag")]
    public async Task Authentication_failure_releases_no_plaintext_from_the_failed_segment(string damage)
    {
        using var output = new MemoryStream();
        using (var writer = await BackupEncryptionStream.CreateAsync(output, Key, Aad, Token))
        {
            await writer.WriteAsync("secret"u8.ToArray(), Token);
            await writer.CompleteAsync(Token);
        }
        var bytes = output.ToArray();
        var key = Key.ToArray();
        var aad = Aad.ToArray();
        if (damage == "key")
            key[0] ^= 1;
        else if (damage == "aad")
            aad[0] ^= 1;
        else if (damage == "salt")
            bytes[1] ^= 1;
        else if (damage == "nonce")
            bytes[33] ^= 1;
        else
            bytes[^1] ^= 1;
        using var input = new MemoryStream(bytes);
        using var reader = await BackupEncryptionStream.OpenAsync(input, key, aad, Token);
        using var recovered = new MemoryStream();
        await Assert.ThrowsAsync<AuthenticationTagMismatchException>(() => reader.CopyToAsync(recovered, Token));
        Assert.Equal(0, recovered.Length);
    }

    [Theory]
    [InlineData("truncate")]
    [InlineData("missing_final")]
    [InlineData("append")]
    [InlineData("reorder")]
    [InlineData("duplicate")]
    public async Task Rejects_missing_final_segments_appended_bytes_and_changed_segment_order(string damage)
    {
        const int size = BackupEncryptionStream.SegmentSize;
        using var output = new MemoryStream();
        using (var writer = await BackupEncryptionStream.CreateAsync(output, Key, Aad, Token))
        {
            await writer.WriteAsync(new byte[size * 3], Token);
            await writer.CompleteAsync(Token);
        }
        var bytes = output.ToArray();
        bytes = damage switch
        {
            "truncate" => bytes[..^1],
            "missing_final" => bytes[..(size * 3)],
            "append" => [.. bytes, 0],
            "reorder" => [.. bytes.AsSpan(0, size), .. bytes.AsSpan(size * 2, size), .. bytes.AsSpan(size, size), .. bytes.AsSpan(size * 3)],
            _ => [.. bytes.AsSpan(0, size * 2), .. bytes.AsSpan(size, size), .. bytes.AsSpan(size * 3)]
        };
        using var input = new BackupShortReadStream(bytes, 65_537);
        using var reader = await BackupEncryptionStream.OpenAsync(input, Key, Aad, Token);
        using var recovered = new MemoryStream();
        await Assert.ThrowsAsync<AuthenticationTagMismatchException>(() => reader.CopyToAsync(recovered, Token));
    }

    [Fact]
    public async Task Disposal_does_not_complete_or_close_an_aborted_export()
    {
        using var output = new MemoryStream();
        using (var writer = await BackupEncryptionStream.CreateAsync(output, Key, Aad, Token))
            await writer.WriteAsync("unfinished"u8.ToArray(), Token);
        Assert.Equal(40, output.Length);
        Assert.True(output.CanWrite);
        output.Position = 0;
        using var reader = await BackupEncryptionStream.OpenAsync(output, Key, Aad, Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.CopyToAsync(Stream.Null, Token));
    }

    [Fact]
    public async Task Reusing_the_input_key_generates_independent_streams()
    {
        using var first = new MemoryStream();
        using var second = new MemoryStream();
        using (var writer = await BackupEncryptionStream.CreateAsync(first, Key, Aad, Token))
            await writer.CompleteAsync(Token);
        using (var writer = await BackupEncryptionStream.CreateAsync(second, Key, Aad, Token))
            await writer.CompleteAsync(Token);
        Assert.NotEqual(first.ToArray().AsSpan(1, 32).ToArray(), second.ToArray().AsSpan(1, 32).ToArray());
        Assert.NotEqual(first.ToArray().AsSpan(33, 7).ToArray(), second.ToArray().AsSpan(33, 7).ToArray());
    }
}

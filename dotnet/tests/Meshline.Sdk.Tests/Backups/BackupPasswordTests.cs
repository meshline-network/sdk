using Meshline.Backups;
using Meshline.Tests.Support;
using System.Security.Cryptography;
using System.Text.Json;

namespace Meshline.Tests.Backups;

public sealed class BackupPasswordTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;
    const string Salt = "AAECAwQFBgcICQoLDA0ODw==";
    static BackupPassword.Parameters Parameters => new("scrypt", Salt, 65536, 8, 2);

    [Fact]
    public async Task Password_encoding_and_scrypt_match_independent_vectors()
    {
        using var json = await BackupFormatSetup.ReadVectorAsync(Token);
        foreach (var vector in json.RootElement.GetProperty("password_vectors").EnumerateArray())
        {
            var parameters = Parameters with
            {
                Salt = Convert.ToBase64String(Convert.FromHexString(vector.GetProperty("salt_hex").GetString()!))
            };
            var key = await BackupPassword.DeriveAsync(vector.GetProperty("password").GetString()!, parameters, Token);
            try
            {
                Assert.Equal(vector.GetProperty("key_hex").GetString(), Convert.ToHexStringLower(key));
            }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("high surrogate")]
    [InlineData("low surrogate")]
    public async Task Rejects_empty_or_invalid_unicode_passwords_without_writing_output(string? password)
    {
        password = password switch
        {
            "high surrogate" => "\ud800",
            "low surrogate" => "\udc00",
            _ => password
        };
        using var output = new MemoryStream();
        await Assert.ThrowsAnyAsync<ArgumentException>(() => BackupCodec.CreateAsync(output, password!, Token));
        Assert.Equal(0, output.Length);
        await using var writer = await BackupCodec.CreateAsync(output, "valid after rejection", Token);
        await writer.CompleteAsync(Token);
        Assert.True(output.Length > 0);
    }

    [Theory]
    [InlineData("\"algorithm\":\"scrypt\"", "\"algorithm\":\"argon2id\"")]
    [InlineData("\"n\":65536", "\"n\":2147483647")]
    [InlineData("\"n\":65536", "\"n\":16384")]
    [InlineData("\"r\":8", "\"r\":2147483647")]
    [InlineData("\"p\":2", "\"p\":2147483647")]
    [InlineData("\"salt\":\"" + Salt + "\"", "\"salt\":null")]
    [InlineData(Salt, "AAECAwQFBgcICQoLDA0ODx==")]
    [InlineData(Salt, "AAECAwQFBgcICQoLDA0ODw")]
    [InlineData(Salt, "!!!!!!!!!!!!!!!!!!!!!!!!")]
    public async Task Rejects_unsupported_work_and_invalid_salts_before_reading_the_body(string field, string replacement)
    {
        var header = JsonSerializer.Serialize(new BackupCodec.Header(Parameters), BackupCodec.Json).Replace(field, replacement, StringComparison.Ordinal);
        using var input = new MemoryStream(BackupFormatSetup.Preamble(header));
        await Assert.ThrowsAsync<InvalidDataException>(() => BackupCodec.OpenAsync(input, "password", Token));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"key_derivation\":null}")]
    [InlineData("{\"key_derivation\":{}}")]
    [InlineData("{\"key_derivation\":{\"algorithm\":\"scrypt\",\"salt\":\"" + Salt + "\",\"n\":65536,\"r\":8}}")]
    [InlineData("{\"key_derivation\":{\"algorithm\":\"scrypt\",\"salt\":\"" + Salt + "\",\"n\":65536,\"r\":8,\"p\":2,\"extra\":1}}")]
    [InlineData("{\"key_derivation\":{\"algorithm\":\"scrypt\",\"salt\":\"" + Salt + "\",\"n\":65536,\"n\":65536,\"r\":8,\"p\":2}}")]
    public async Task Rejects_missing_duplicate_or_unknown_derivation_fields(string header)
    {
        using var input = new MemoryStream(BackupFormatSetup.Preamble(header));
        var error = await Record.ExceptionAsync(() => BackupCodec.OpenAsync(input, "password", Token));
        Assert.True(error is JsonException or InvalidDataException, error?.ToString());
    }

    [Fact]
    public async Task Repeated_exports_use_independent_salts_and_accept_equivalent_unicode_passwords()
    {
        using var first = new MemoryStream();
        using var second = new MemoryStream();
        foreach (var output in new[] { first, second })
        {
            await using var writer = await BackupCodec.CreateAsync(output, " 备份🔐 cafe\u0301 ", Token);
            await writer.WriteAsync("end", new
            {
                records = 0
            }, Token);
            await writer.CompleteAsync(Token);
            output.Position = 0;
        }
        var firstHeader = await BackupCodec.ReadHeaderAsync(first, Token);
        var secondHeader = await BackupCodec.ReadHeaderAsync(second, Token);
        Assert.NotEqual(firstHeader.Header.KeyDerivation.Salt, secondHeader.Header.KeyDerivation.Salt);
        Assert.False(first.ToArray().AsSpan().SequenceEqual(second.ToArray()));
        foreach (var output in new[] { first, second })
        {
            output.Position = 0;
            await using var reader = await BackupCodec.OpenAsync(output, " 备份🔐 café ", Token);
            Assert.Equal("end", (await reader.ReadAsync(Token)).GetProperty("type").GetString());
            await reader.RequireEndAsync(Token);
        }
        foreach (var password in new[] { "备份🔐 café", " 备份🔐 CAFÉ ", "another password" })
        {
            first.Position = 0;
            await using var reader = await BackupCodec.OpenAsync(first, password, Token);
            await Assert.ThrowsAsync<AuthenticationTagMismatchException>(() => reader.ReadAsync(Token));
        }
    }

    [Fact]
    public async Task Cancellation_releases_derivation_capacity_and_does_not_write_a_partial_header()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        using var first = new MemoryStream();
        using var queued = new MemoryStream();
        var active = BackupCodec.CreateAsync(first, "password", cancellation.Token);
        var waiting = BackupCodec.CreateAsync(queued, "password", cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(0, first.Length);
        Assert.Equal(0, queued.Length);
        await using var next = await BackupCodec.CreateAsync(first, "password", Token);
        await next.CompleteAsync(Token);
        Assert.True(first.Length > 0);
    }
}

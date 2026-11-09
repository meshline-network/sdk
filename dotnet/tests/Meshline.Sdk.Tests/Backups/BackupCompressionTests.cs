using Meshline.Backups;
using Meshline.Tests.Support;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Meshline.Tests.Backups;

public sealed class BackupCompressionTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;
    const string Password = "compression test password";
    const string End = "{\"type\":\"end\",\"data\":{\"records\":0}}";

    [Fact]
    public async Task Codec_exports_a_complete_standard_gzip_stream()
    {
        using var backup = new MemoryStream();
        await using (var writer = await BackupCodec.CreateAsync(backup, Password, Token))
        {
            await writer.WriteAsync("end", new
            {
                records = 0
            }, Token);
            await writer.CompleteAsync(Token);
        }
        using var compressed = new MemoryStream(await BackupFormatSetup.DecryptAsync(backup.ToArray(), Password, Token));
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        using var plaintext = new MemoryStream();
        await gzip.CopyToAsync(plaintext, Token);
        Assert.Equal(BackupFormatSetup.Records(End), plaintext.ToArray());
        Assert.True(backup.CanWrite);
    }

    [Theory]
    [InlineData("missing_trailer")]
    [InlineData("truncated_trailer")]
    [InlineData("trailing_byte")]
    [InlineData("empty_member")]
    [InlineData("long_header")]
    public async Task Authenticated_input_uses_standard_decoder_tolerance(string variation)
    {
        var compressed = await BackupFormatSetup.CompressAsync(BackupFormatSetup.Records(End), Token);
        if (variation == "missing_trailer")
            compressed = compressed[..^8];
        else if (variation == "truncated_trailer")
            compressed = compressed[..^1];
        else if (variation == "trailing_byte")
            compressed = [.. compressed, 0];
        else if (variation == "empty_member")
            compressed = [.. compressed, .. await BackupFormatSetup.CompressAsync([], Token)];
        else
        {
            compressed[3] = 8;
            compressed = [.. compressed.AsSpan(0, 10), .. Enumerable.Repeat((byte)'x', 65536), 0, .. compressed.AsSpan(10)];
        }
        var bytes = await BackupFormatSetup.EncryptAsync(compressed, Password, Token);
        using var input = new BackupShortReadStream(bytes, 137);
        await using var reader = await BackupCodec.OpenAsync(input, Password, Token);
        Assert.Equal("end", (await reader.ReadAsync(Token)).GetProperty("type").GetString());
        await reader.RequireEndAsync(Token);
        Assert.Equal(input.Length, input.Position);
    }

    [Theory]
    [InlineData("crc")]
    [InlineData("size")]
    [InlineData("flags")]
    public async Task Standard_decoder_errors_propagate(string damage)
    {
        var compressed = await BackupFormatSetup.CompressAsync(BackupFormatSetup.Records(End), Token);
        if (damage == "crc")
            compressed[^8] ^= 1;
        else if (damage == "size")
            compressed[^4] ^= 1;
        else
            compressed[3] |= 0x80;
        var bytes = await BackupFormatSetup.EncryptAsync(compressed, Password, Token);
        using var input = new BackupShortReadStream(bytes, 1);
        await using var reader = await BackupCodec.OpenAsync(input, Password, Token);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await reader.ReadAsync(Token);
            await reader.RequireEndAsync(Token);
        });
    }

    [Fact]
    public async Task Decoded_data_from_a_second_member_after_end_is_rejected()
    {
        var first = await BackupFormatSetup.CompressAsync(BackupFormatSetup.Records(End), Token);
        var second = await BackupFormatSetup.CompressAsync([1], Token);
        var bytes = await BackupFormatSetup.EncryptAsync([.. first, .. second], Password, Token);
        using var input = new BackupShortReadStream(bytes, 1);
        await using var reader = await BackupCodec.OpenAsync(input, Password, Token);
        Assert.Equal("end", (await reader.ReadAsync(Token)).GetProperty("type").GetString());
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.RequireEndAsync(Token));
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("tag")]
    [InlineData("truncate")]
    [InlineData("missing_final")]
    [InlineData("append")]
    public async Task Early_gzip_eof_still_authenticates_remaining_encryption_segments(string damage)
    {
        var compressed = await BackupFormatSetup.CompressAsync(BackupFormatSetup.Records(End), Token);
        var bytes = await BackupFormatSetup.EncryptAsync([.. compressed, .. new byte[BackupEncryptionStream.SegmentSize * 3]], Password, Token);
        if (damage == "tag")
            bytes[^1] ^= 1;
        else if (damage == "truncate")
            bytes = bytes[..^1];
        else if (damage == "missing_final")
            bytes = bytes[..(12 + BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(8)) + BackupEncryptionStream.SegmentSize * 3)];
        else if (damage == "append")
            bytes = [.. bytes, 0];
        using var input = new BackupShortReadStream(bytes, 65_537);
        await using var reader = await BackupCodec.OpenAsync(input, Password, Token);
        Assert.Equal("end", (await reader.ReadAsync(Token)).GetProperty("type").GetString());
        Assert.True(input.Position < input.Length);
        if (damage == "valid")
        {
            await reader.RequireEndAsync(Token);
            Assert.Equal(input.Length, input.Position);
        }
        else
            await Assert.ThrowsAsync<AuthenticationTagMismatchException>(() => reader.RequireEndAsync(Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Aborted_export_cleanup_preserves_the_failure_and_does_not_write_or_close_the_destination(bool cancel)
    {
        using var output = new MemoryStream();
        Exception failure = cancel ? new OperationCanceledException(Token) : new IOException("export interrupted");
        long lengthBeforeDisposal = 0;
        var error = await Record.ExceptionAsync(async () =>
        {
            await using var writer = await BackupCodec.CreateAsync(output, Password, Token);
            await writer.WriteAsync("end", new
            {
                records = 0
            }, Token);
            lengthBeforeDisposal = output.Length;
            throw failure;
        });
        Assert.Same(failure, error);
        Assert.Equal(lengthBeforeDisposal, output.Length);
        Assert.True(output.CanWrite);
        await Assert.ThrowsAsync<InvalidDataException>(() => BackupFormatSetup.DecryptAsync(output.ToArray(), Password, Token));
    }

    [Fact]
    public async Task Completing_compression_remains_cancelable_at_an_encryption_segment_boundary()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        using var output = new PausedCompletionStream();
        await using var codec = await BackupCodec.CreateAsync(output, Password, cancellation.Token);
        var bytes = new byte[1_100_000];
        new Random(12345).NextBytes(bytes);
        var low = 1_000_000;
        var high = bytes.Length;
        var capacity = BackupEncryptionStream.SegmentSize - BackupEncryptionStream.HeaderSize - 16;
        // Locate a boundary using the current platform compressor, without relying on
        // a particular runtime's compressed bytes. Finalizing gzip must emit segment 0.
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (CompressedLength(mid) <= capacity)
                low = mid + 1;
            else
                high = mid;
        }
        var content = Convert.ToBase64String(bytes.AsSpan(0, low));
        await codec.WriteAsync("probe", new
        {
            text = content[..700000]
        }, cancellation.Token);
        await codec.WriteAsync("probe", new
        {
            text = content[700000..]
        }, cancellation.Token);
        await codec.WriteAsync("end", new
        {
            records = 2
        }, cancellation.Token);
        output.Finishing = true;
        var completion = codec.CompleteAsync(cancellation.Token);
        try
        {
            await output.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.Equal(BackupEncryptionStream.SegmentSize - BackupEncryptionStream.HeaderSize, output.BlockedLength);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion.WaitAsync(TimeSpan.FromSeconds(5), Token));
        }
        finally
        {
            output.Release.TrySetResult();
            _ = await Record.ExceptionAsync(() => completion);
        }
        var length = output.Length;
        await codec.DisposeAsync();
        Assert.Equal(length, output.Length);
        Assert.True(output.CanWrite);

        long CompressedLength(int size)
        {
            var text = Convert.ToBase64String(bytes.AsSpan(0, size));
            var records = BackupFormatSetup.Records(
                BackupData.Record("probe", new
                {
                    text = text[..700000]
                }).GetRawText(),
                BackupData.Record("probe", new
                {
                    text = text[700000..]
                }).GetRawText(),
                BackupData.Record("end", new
                {
                    records = 2
                }).GetRawText());
            using var compressed = new MemoryStream();
            using (var gzip = new GZipStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
                gzip.Write(records);
            return compressed.Length;
        }
    }

    sealed class PausedCompletionStream : MemoryStream
    {
        public bool Finishing
        {
            get; set;
        }
        public int BlockedLength
        {
            get; private set;
        }
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Finishing)
            {
                BlockedLength = buffer.Length;
                Blocked.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            await base.WriteAsync(buffer, cancellationToken);
        }
    }
}

using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Meshline.Backups;

// Tink AES-GCM-HKDF Streaming, RAW, SHA256, 32-byte keys, 1 MiB segments, offset 0.
// https://developers.google.com/tink/streaming-aead/aes_gcm_hkdf_streaming
// The enclosing Meshline header is associated data, not part of the Tink stream offset.
internal sealed class BackupEncryptionStream : Stream
{
    internal const int SegmentSize = 1_048_576;
    internal const int HeaderSize = 40;
    const int TagSize = 16;
    readonly Stream _stream;
    readonly bool _writing;
    readonly CancellationToken _writeCancellationToken;
    readonly AesGcm _cipher;
    readonly byte[] _nonce = new byte[12];
    readonly byte[] _plaintext = new byte[SegmentSize - TagSize];
    readonly byte[] _ciphertext = new byte[SegmentSize + 1];
    int _capacity = SegmentSize - HeaderSize - TagSize;
    int _length;
    int _position;
    int _lookahead = -1;
    ulong _index;
    bool _complete;
    bool _failed;
    bool _disposed;

    BackupEncryptionStream(Stream stream, ReadOnlySpan<byte> key, ReadOnlySpan<byte> associatedData, byte[] header, bool writing, CancellationToken writeCancellationToken = default)
    {
        var fileKey = new byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, key, fileKey, header.AsSpan(1, 32), associatedData);
        try
        {
            _cipher = new AesGcm(fileKey, TagSize);
        }
        finally { CryptographicOperations.ZeroMemory(fileKey); }
        header.AsSpan(33, 7).CopyTo(_nonce);
        _stream = stream;
        _writing = writing;
        _writeCancellationToken = writeCancellationToken;
    }

    internal static async Task<BackupEncryptionStream> CreateAsync(Stream stream, ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> associatedData, CancellationToken token)
    {
        ValidateKey(key);
        var header = RandomNumberGenerator.GetBytes(HeaderSize);
        header[0] = HeaderSize;
        var result = new BackupEncryptionStream(stream, key.Span, associatedData.Span, header, writing: true, token);
        try
        {
            await stream.WriteAsync(header, token).ConfigureAwait(false);
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    internal static async Task<BackupEncryptionStream> OpenAsync(Stream stream, ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> associatedData, CancellationToken token)
    {
        ValidateKey(key);
        var header = new byte[HeaderSize];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        if (header[0] != HeaderSize)
            throw new InvalidDataException("Invalid backup encryption header.");
        return new(stream, key.Span, associatedData.Span, header, writing: false);
    }

    static void ValidateKey(ReadOnlyMemory<byte> key)
    {
        if (key.Length != 32)
            throw new ArgumentException("A backup key must contain exactly 32 bytes.", nameof(key));
    }

    void Check(bool writing)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (writing != _writing)
            throw new NotSupportedException();
        if (_failed)
            throw new InvalidOperationException("The backup encryption stream has failed.");
        if (writing && _complete)
            throw new InvalidOperationException("The backup encryption stream is complete.");
    }

    void SetNonce(bool final)
    {
        if (_index > uint.MaxValue || (!final && _index == uint.MaxValue))
            throw new InvalidDataException("Too many backup encryption segments.");
        BinaryPrimitives.WriteUInt32BigEndian(_nonce.AsSpan(7, 4), (uint)_index);
        _nonce[11] = final ? (byte)1 : (byte)0;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Check(writing: true);
        try
        {
            // GZipStream.DisposeAsync finishes compression with tokenless writes.
            // Keep those writes bound to the enclosing export operation as well.
            if (!cancellationToken.CanBeCanceled)
                cancellationToken = _writeCancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            while (!buffer.IsEmpty)
            {
                // Keep a full segment until another byte arrives: it may be the final segment.
                if (_length == _capacity)
                    await WriteSegmentAsync(final: false, cancellationToken).ConfigureAwait(false);
                var count = Math.Min(buffer.Length, _capacity - _length);
                buffer[..count].CopyTo(_plaintext.AsMemory(_length));
                _length += count;
                buffer = buffer[count..];
            }
        }
        catch { _failed = true; throw; }
    }

    async Task WriteSegmentAsync(bool final, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        SetNonce(final);
        _cipher.Encrypt(_nonce, _plaintext.AsSpan(0, _length), _ciphertext.AsSpan(0, _length), _ciphertext.AsSpan(_length, TagSize));
        await _stream.WriteAsync(_ciphertext.AsMemory(0, _length + TagSize), token).ConfigureAwait(false);
        CryptographicOperations.ZeroMemory(_plaintext.AsSpan(0, _length));
        _length = 0;
        _capacity = SegmentSize - TagSize;
        _index++;
    }

    internal async Task CompleteAsync(CancellationToken token)
    {
        Check(writing: true);
        try
        {
            await WriteSegmentAsync(final: true, token).ConfigureAwait(false);
            _complete = true;
        }
        catch { _failed = true; throw; }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Check(writing: false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (buffer.IsEmpty)
                return 0;
            if (_position == _length)
            {
                if (_complete)
                    return 0;
                await ReadSegmentAsync(cancellationToken).ConfigureAwait(false);
            }
            var count = Math.Min(buffer.Length, _length - _position);
            _plaintext.AsMemory(_position, count).CopyTo(buffer);
            CryptographicOperations.ZeroMemory(_plaintext.AsSpan(_position, count));
            _position += count;
            return count;
        }
        catch { _failed = true; throw; }
    }

    async Task ReadSegmentAsync(CancellationToken token)
    {
        var capacity = _capacity + TagSize;
        var count = 0;
        if (_lookahead >= 0)
            _ciphertext[count++] = (byte)_lookahead;
        count += await _stream.ReadAtLeastAsync(_ciphertext.AsMemory(count, capacity + 1 - count), capacity + 1 - count, throwOnEndOfStream: false, token).ConfigureAwait(false);
        var final = count <= capacity;
        _lookahead = final ? -1 : _ciphertext[capacity];
        if (!final)
            count--;
        if (count < TagSize || (_index > 0 && count == TagSize))
            throw new InvalidDataException("Missing or empty backup encryption segment.");
        SetNonce(final);
        var length = count - TagSize;
        // No bytes leave this stream until authentication of the entire segment succeeds.
        _cipher.Decrypt(_nonce, _ciphertext.AsSpan(0, length), _ciphertext.AsSpan(length, TagSize), _plaintext.AsSpan(0, length));
        _length = length;
        _position = 0;
        _capacity = SegmentSize - TagSize;
        _index++;
        _complete = final;
    }

    public override bool CanRead => !_disposed && !_writing;
    public override bool CanWrite => !_disposed && _writing && !_complete && !_failed;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException(); set => throw new NotSupportedException();
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Use asynchronous backup I/O.");
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("Use asynchronous backup I/O.");
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override void Flush() => throw new NotSupportedException("Use asynchronous backup I/O.");
    public override Task FlushAsync(CancellationToken cancellationToken) => _stream.FlushAsync(cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _cipher.Dispose();
            CryptographicOperations.ZeroMemory(_plaintext);
            CryptographicOperations.ZeroMemory(_ciphertext);
        }
        // Disposing aborts; only CompleteAsync writes a final segment. Leave the caller's stream open.
        base.Dispose(disposing);
    }
}

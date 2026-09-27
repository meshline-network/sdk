using Meshline.Models.Protocol;
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Meshline.Tests.Support;

// A server peer on an in-memory duplex stream. The SDK uses a real ClientWebSocket,
// including its HTTP upgrade validation, framing, masking, and cancellation.
internal sealed class MemorySocket : IDisposable, IAsyncDisposable
{
    readonly DuplexStream clientStream;
    readonly WebSocket server;
    readonly CancellationTokenSource lifetime = new();
    readonly Channel<(byte[] Bytes, int FragmentSize, WebSocketMessageType Type)> outgoing = Channel.CreateUnbounded<(byte[], int, WebSocketMessageType)>();
    readonly Func<RpcRequest, MemorySocket, Task> onRequest;
    int disposed;
    public ConcurrentQueue<RpcRequest> Requests { get; } = new();
    public Task Completion { get; }
    public bool ClientStreamDisposed => clientStream.IsDisposed;

    MemorySocket(DuplexStream client, DuplexStream remote, Func<RpcRequest, MemorySocket, Task> handler)
    {
        clientStream = client;
        onRequest = handler;
        server = WebSocket.CreateFromStream(remote, new WebSocketCreationOptions
        {
            IsServer = true,
            KeepAliveInterval = TimeSpan.Zero
        });
        Completion = Task.WhenAll(ReceiveAsync(), SendAsync());
    }

    public static HttpResponseMessage Upgrade(HttpRequestMessage request, Func<RpcRequest, MemorySocket, Task> handler, out MemorySocket peer)
    {
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Contains(request.Headers.Connection, value => value.Equals("Upgrade", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("13", Assert.Single(request.Headers.GetValues("Sec-WebSocket-Version")));

        var key = Assert.Single(request.Headers.GetValues("Sec-WebSocket-Key"));
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        var (client, remote) = DuplexStream.CreatePair();
        peer = new(client, remote, handler);
        var response = new HttpResponseMessage(HttpStatusCode.SwitchingProtocols)
        {
            Version = HttpVersion.Version11,
            RequestMessage = request,
            Content = new UpgradeContent(client)
        };
        response.Headers.Connection.Add("Upgrade");
        response.Headers.Upgrade.ParseAdd("websocket");
        response.Headers.Add("Sec-WebSocket-Accept", accept);
        return response;
    }

    public void Push(string json, int fragmentSize = 16000)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fragmentSize);
        Enqueue(Encoding.UTF8.GetBytes(json), fragmentSize, WebSocketMessageType.Text);
    }

    public void Reply(string id, string result) => Push("{\"jsonrpc\":\"2.0\",\"id\":" + JsonSerializer.Serialize(id) + ",\"result\":" + result + "}");
    public void Disconnect() => Enqueue([], 1, WebSocketMessageType.Close);
    public void Binary() => Enqueue([1], 1, WebSocketMessageType.Binary);
    public void Abort() => clientStream.Fail(new IOException("Injected connection loss"));
    void Enqueue(byte[] bytes, int fragmentSize, WebSocketMessageType type)
    {
        lifetime.Token.ThrowIfCancellationRequested();
        if (!outgoing.Writer.TryWrite((bytes, fragmentSize, type)))
        {
            // Stop can cancel and complete the queue between the check and TryWrite.
            // A callback already in progress must observe that cancellation as well.
            lifetime.Token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The offline WebSocket peer is closed.");
        }
    }

    async Task ReceiveAsync()
    {
        var buffer = new byte[16384];
        try
        {
            using var message = new MemoryStream();
            while (true)
            {
                var result = await server.ReceiveAsync(buffer.AsMemory(), lifetime.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    return;

                Assert.Equal(WebSocketMessageType.Text, result.MessageType);

                message.Write(buffer.AsSpan(0, result.Count));
                if (!result.EndOfMessage)
                    continue;
                var request = ProtocolModel.FromJson<RpcRequest>(Encoding.UTF8.GetString(message.GetBuffer(), 0, checked((int)message.Length)))!;
                message.SetLength(0);
                Requests.Enqueue(request);
                await onRequest(request, this).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (WebSocketException) when (clientStream.IsClosed)
        {
        }
        finally
        {
            Stop();
        }
    }

    async Task SendAsync()
    {
        try
        {
            await foreach (var frame in outgoing.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
            {
                if (frame.Type == WebSocketMessageType.Close)
                {
                    await server.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Injected close", lifetime.Token).ConfigureAwait(false);
                    return;
                }

                for (var offset = 0; offset < frame.Bytes.Length; offset += frame.FragmentSize)
                {
                    var count = Math.Min(frame.FragmentSize, frame.Bytes.Length - offset);
                    await server.SendAsync(frame.Bytes.AsMemory(offset, count), frame.Type, offset + count == frame.Bytes.Length, lifetime.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (WebSocketException) when (clientStream.IsClosed)
        {
        }
        finally
        {
            Stop();
        }
    }

    void Stop()
    {
        lifetime.Cancel();
        outgoing.Writer.TryComplete();
        server.Dispose();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        Stop();
        try
        {
            await Completion.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        finally
        {
            clientStream.Dispose();
            lifetime.Dispose();
        }
    }

    // StreamContent exposes a read-only wrapper; an upgraded connection must keep
    // the original writable stream, just like the HTTP handler's upgrade response.
    sealed class UpgradeContent(Stream stream) : HttpContent
    {
        protected override Stream CreateContentReadStream(CancellationToken cancellationToken) => stream;
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult(stream);
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => Task.FromResult(stream);
        protected override Task SerializeToStreamAsync(Stream target, TransportContext? context) => throw new NotSupportedException("An upgraded connection must not be buffered.");
        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                stream.Dispose();
            base.Dispose(disposing);
        }
    }

    sealed class DuplexStream(Channel<byte[]> input, Channel<byte[]> output, DuplexStream.Link link) : Stream
    {
        ReadOnlyMemory<byte> pending;
        int disposed;
        public bool IsDisposed => Volatile.Read(ref disposed) != 0;
        public bool IsClosed => Volatile.Read(ref link.Closed) != 0;
        public override bool CanRead => !IsDisposed;
        public override bool CanWrite => !IsDisposed;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public static (DuplexStream Client, DuplexStream Server) CreatePair()
        {
            var left = Channel.CreateUnbounded<byte[]>();
            var right = Channel.CreateUnbounded<byte[]>();
            var link = new Link(left, right);
            return (new(left, right, link), new(right, left, link));
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (buffer.IsEmpty)
                return 0;
            while (pending.IsEmpty)
            {
                if (!await input.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                    return 0;
                if (input.Reader.TryRead(out var bytes))
                    pending = bytes;
            }

            var count = Math.Min(buffer.Length, pending.Length);
            pending[..count].CopyTo(buffer);
            pending = pending[count..];
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (IsClosed)
                throw new IOException("The in-memory connection is closed.");
            if (!buffer.IsEmpty && !output.Writer.TryWrite(buffer.ToArray()))
                throw new IOException("The in-memory peer has stopped reading.");
        }

        public void Fail(Exception exception) => link.Close(exception);
        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref disposed, 1) == 0)
                link.Close();
            base.Dispose(disposing);
        }

        public sealed class Link(Channel<byte[]> left, Channel<byte[]> right)
        {
            public int Closed;
            public void Close(Exception? exception = null)
            {
                if (Interlocked.Exchange(ref Closed, 1) != 0)
                    return;
                left.Writer.TryComplete(exception);
                right.Writer.TryComplete(exception);
            }
        }
    }
}

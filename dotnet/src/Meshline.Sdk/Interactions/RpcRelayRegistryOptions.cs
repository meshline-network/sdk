using Meshline.Models;

namespace Meshline.Interactions;

/// <summary>Explicit configuration for read-only Neo N3 Registry RPC access.</summary>
public sealed record RpcRelayRegistryOptions
{
    /// <summary>The trusted network reference and Registry contract.</summary>
    public required NetworkContext Context { get; init; }
    /// <summary>The absolute HTTP(S) RPC URL. Credentials belong in the supplied HTTP client.</summary>
    public required Uri RpcUrl { get; init; }
    /// <summary>The deadline for each RPC request, including response body reads. Defaults to 15 seconds.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>The number of entries requested per iterator page, from 1 through 1000.</summary>
    public int IteratorPageSize { get; init; } = 100;

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Context);
        ArgumentNullException.ThrowIfNull(RpcUrl);
        if (!RpcUrl.IsAbsoluteUri || RpcUrl.Scheme is not ("https" or "http") || RpcUrl.UserInfo.Length != 0 || RpcUrl.Fragment.Length != 0)
            throw new ArgumentException("Expected an absolute HTTP(S) RPC URL without user information or a fragment.", nameof(RpcUrl));
        if (RequestTimeout <= TimeSpan.Zero || RequestTimeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout));
        if (IteratorPageSize is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(IteratorPageSize));
    }
}

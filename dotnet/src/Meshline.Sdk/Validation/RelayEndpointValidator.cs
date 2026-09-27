using Multiformats.Address;
using Multiformats.Address.Protocols;
using Multiformats.Base;
using Multiformats.Hash;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Meshline.Validation;

static class RelayEndpointValidator
{
    public static ProtocolViolation? Validate(string endpoint, out RelayEndpointKind kind, out string? peerId)
    {
        kind = RelayEndpointKind.Unknown;
        peerId = null;
        if (string.IsNullOrEmpty(endpoint))
            return new(ProtocolViolationKind.Format, "Relay endpoints must be nonempty.");

        var candidate = endpoint.AsSpan().TrimStart();
        var isHttps = candidate.StartsWith("https:", StringComparison.OrdinalIgnoreCase);
        var isWss = candidate.StartsWith("wss:", StringComparison.OrdinalIgnoreCase);
        if (isHttps || isWss)
        {
            kind = isHttps ? RelayEndpointKind.Https : RelayEndpointKind.Wss;
            if (!IsValidWebEndpoint(endpoint, isHttps ? "https://" : "wss://"))
                return new(ProtocolViolationKind.Format, "Invalid HTTPS or WSS relay endpoint.");
        }
        else if (candidate.StartsWith("/", StringComparison.Ordinal) && (candidate.Contains("/tcp/", StringComparison.Ordinal) || candidate.EndsWith("/tcp", StringComparison.Ordinal)))
        {
            kind = RelayEndpointKind.Libp2pTcp;
            if (!TryGetPeerId(endpoint, out peerId))
                return new(ProtocolViolationKind.Format, "Invalid libp2p TCP relay endpoint.");
        }

        return null;
    }

    static bool IsValidWebEndpoint(string endpoint, string prefix)
    {
        if (!endpoint.StartsWith(prefix, StringComparison.Ordinal) || Encoding.UTF8.GetByteCount(endpoint) > 512
            || endpoint.Any(static character => char.IsWhiteSpace(character) || char.IsControl(character))
            || endpoint.IndexOfAny(['?', '#', '\\']) >= 0
            || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || !uri.IsWellFormedOriginalString()
            || uri.HostNameType == UriHostNameType.Unknown)
            return false;

        var authority = endpoint.AsSpan(prefix.Length);
        var slash = authority.IndexOf('/');
        if (slash >= 0)
            authority = authority[..slash];
        return !authority.Contains('@');
    }

    static bool TryGetPeerId(string endpoint, out string? peerId)
    {
        peerId = null;
        if (!endpoint.StartsWith('/') || endpoint.EndsWith('/') || endpoint.Contains("//", StringComparison.Ordinal)
            || endpoint.Any(static character => char.IsWhiteSpace(character) || char.IsControl(character)))
            return false;

        var parts = endpoint.Split('/');
        if (parts.Length < 4 || parts[^2] != "p2p")
            return false;
        for (var i = 1; i < parts.Length; i++)
        {
            if (parts[i] == "tcp" && (i + 1 == parts.Length || !parts[i + 1].All(char.IsAsciiDigit)))
                return false;
        }

        try
        {
            var peerText = parts[^1];
            byte[] hashBytes;
            if (peerText.StartsWith('1') || peerText.StartsWith("Qm", StringComparison.Ordinal))
            {
                if (!Multibase.TryDecode("z" + peerText, out _, out hashBytes))
                    return false;
            }
            else
            {
                if (!Multibase.TryDecode(peerText, out _, out var cid) || cid.Length < 3 || cid[0] != 1 || cid[1] != 0x72)
                    return false;
                hashBytes = cid[2..];
            }

            var hash = Multihash.Decode(hashBytes);
            if (!((int)hash.Code == 0x12 && hash.Length == 32 || (int)hash.Code == 0 && hash.Length is > 0 and <= 42)
                || !hashBytes.AsSpan().SequenceEqual((byte[])hash))
                return false;

            var address = Multiaddress.Decode(endpoint[..endpoint.LastIndexOf("/p2p/", StringComparison.Ordinal)]).Add<P2P>(hash);
            if (address.Protocols.Count < 3 || address.Protocols.Count(static protocol => protocol is TCP) != 1
                || address.Protocols.Count(static protocol => protocol is P2P) != 1
                || address.Protocols[1] is not TCP { Port: > 0 }
                || address.Protocols[^1] is not P2P peer)
                return false;

            var validHost = address.Protocols[0] switch
            {
                IP4 { Value: IPAddress ip } => ip.AddressFamily == AddressFamily.InterNetwork,
                IP6 { Value: IPAddress ip } => ip.AddressFamily == AddressFamily.InterNetworkV6,
                DNS { Value: string host } => Uri.CheckHostName(host) == UriHostNameType.Dns,
                DNS4 { Value: string host } => Uri.CheckHostName(host) == UriHostNameType.Dns,
                DNS6 { Value: string host } => Uri.CheckHostName(host) == UriHostNameType.Dns,
                _ => false
            };
            if (!validHost)
                return false;

            peerId = peer.ToString();
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException or NotSupportedException or IndexOutOfRangeException || exception.GetType() == typeof(Exception))
        {
            return false;
        }
    }
}

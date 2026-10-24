using System.Text;
using System.Text.Json;

namespace Meshline.Validation;

static class PushGatewayValidator
{
    public static ProtocolViolation? Validate(string gateway)
    {
        try { ProtocolStringValidator.Validate(gateway); }
        catch (JsonException exception) { return new(ProtocolViolationKind.Format, exception.Message); }

        if (!gateway.StartsWith("https://", StringComparison.Ordinal) || Encoding.UTF8.GetByteCount(gateway) > 512
            || gateway.Any(static character => char.IsWhiteSpace(character) || char.IsControl(character))
            || gateway.IndexOfAny(['#', '\\']) >= 0
            || !Uri.TryCreate(gateway, UriKind.Absolute, out var uri) || !uri.IsWellFormedOriginalString()
            || uri.HostNameType == UriHostNameType.Unknown)
            return new(ProtocolViolationKind.Format, "The push gateway must be an absolute HTTPS URL of at most 512 UTF-8 bytes, without whitespace, fragment, userinfo or backslash.");

        // Query/path text may contain '@'; only authority userinfo is forbidden.
        var authority = gateway.AsSpan("https://".Length);
        var end = authority.IndexOfAny('/', '?');
        if (end >= 0) authority = authority[..end];
        return authority.IsEmpty || authority.Contains('@')
            ? new(ProtocolViolationKind.Format, "The push gateway requires a valid host and cannot contain userinfo.")
            : null;
    }
}

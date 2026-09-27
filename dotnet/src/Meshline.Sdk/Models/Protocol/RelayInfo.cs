using Meshline.Identity;
using Meshline.Validation;
using System.Text;

namespace Meshline.Models.Protocol;

/// <summary>
/// Contains a relay's public name, server time, and advertised operational limits.
/// </summary>
public sealed record RelayInfo : ProtocolModel
{
    /// <summary>
    /// The relay's lowercase Neo script hash, including the <c>0x</c> prefix.
    /// </summary>
    public required string RelayId { get; init; }
    /// <summary>
    /// The display name.
    /// </summary>
    public required string Name { get; init; }
    /// <summary>
    /// The relay's current time, in Unix seconds.
    /// </summary>
    public required long ServerTime { get; init; }
    /// <summary>
    /// The relay's advertised retention and capacity limits.
    /// </summary>
    public required RelayLimits Limits { get; init; }

    /// <summary>
    /// Validates the relay identifier, display name, server time, and advertised limits.
    /// </summary>
    /// <param name="context">The optional network context passed to nested validation when needed; field-only validation may ignore it.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    public override ProtocolViolation? Validate(NetworkContext? context = null)
    {
        if (RelayIdentity.ValidateRelayId(RelayId) is { } violation)
            return violation;
        if (string.IsNullOrWhiteSpace(Name) || Encoding.UTF8.GetByteCount(Name) > 256)
            return new(ProtocolViolationKind.Format, "The relay name must contain non-whitespace text and cannot exceed 256 UTF-8 bytes.");
        if (ServerTime < 0)
            return new(ProtocolViolationKind.Time, "The relay time must be nonnegative.");
        return Limits.Validate();
    }
}

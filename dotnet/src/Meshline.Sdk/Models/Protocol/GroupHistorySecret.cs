using Meshline.Validation;
using System.Collections.Immutable;
using System.Text;

namespace Meshline.Models.Protocol;

/// <summary>
/// Contains the application secret needed to decrypt messages from a historical group epoch.
/// </summary>
public sealed record GroupHistorySecret : ProtocolModel
{
    /// <summary>
    /// The group's canonical <c>grp_</c> identifier.
    /// </summary>
    public required string GroupId { get; init; }
    /// <summary>
    /// The group key epoch associated with this record.
    /// </summary>
    public required long Epoch { get; init; }
    /// <summary>
    /// The 32-byte application secret for this historical group epoch; treat it as secret material.
    /// </summary>
    public required ImmutableArray<byte> ApplicationSecret { get; init; }

    /// <summary>
    /// Validates the group identifier, epoch, and application-secret length.
    /// </summary>
    /// <param name="context">The optional network context passed to nested validation when needed; field-only validation may ignore it.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    public override ProtocolViolation? Validate(NetworkContext? context = null)
    {
        if (Identifiers.ValidateGroupId(GroupId) is { } groupViolation)
            return groupViolation;
        if (Epoch < 0)
            return new(ProtocolViolationKind.Format, "The group key epoch must be nonnegative.");
        return ApplicationSecret.AsSpan().Length != 32
            ? new(ProtocolViolationKind.Format, "The application secret must contain 32 bytes.")
            : null;
    }

    /// <summary>
    /// Appends the record's diagnostic fields while omitting sensitive material from its string representation.
    /// </summary>
    /// <param name="builder">The builder to which diagnostic fields are appended.</param>
    /// <returns><see langword="true"/> after appending the record's diagnostic fields.</returns>
    protected override bool PrintMembers(StringBuilder builder)
    {
        if (base.PrintMembers(builder))
            builder.Append(", ");

        builder.Append($"{nameof(GroupId)} = {GroupId}, {nameof(Epoch)} = {Epoch}");
        return true;
    }
}

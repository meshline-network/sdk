using Meshline.Validation;

namespace Meshline.Models.Protocol;

/// <summary>
/// Selects a cursor-based page of group administration records.
/// </summary>
public sealed record GroupListQuery : ProtocolModel
{
    /// <summary>
    /// The group's canonical <c>grp_</c> identifier.
    /// </summary>
    public required string GroupId { get; init; }
    /// <summary>
    /// The cursor returned by the preceding page, or <see langword="null"/> for the first page.
    /// </summary>
    public string? Cursor { get; init; }
    /// <summary>
    /// The requested maximum page size, or <see langword="null"/> to use the operation's default.
    /// </summary>
    public long? Limit { get; init; }

    /// <summary>
    /// Validates the group identifier, continuation cursor alphabet, and page limit.
    /// </summary>
    /// <param name="context">The optional network context passed to nested validation when needed; field-only validation may ignore it.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    public override ProtocolViolation? Validate(NetworkContext? context = null)
    {
        if (Identifiers.ValidateGroupId(GroupId) is { } groupViolation)
            return groupViolation;
        if (Cursor is { } cursor && (cursor.Length == 0 || cursor.Any(static character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '~' or '-'))))
            return new(ProtocolViolationKind.Format, "A cursor must be nonempty and contain only ASCII letters, digits, '.', '_', '~' or '-'.");
        return Limit is <= 0
            ? new(ProtocolViolationKind.Format, "The page limit must be positive when specified.")
            : null;
    }
}

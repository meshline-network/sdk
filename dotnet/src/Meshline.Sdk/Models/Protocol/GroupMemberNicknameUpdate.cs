using Meshline.Validation;
using System.Text;

namespace Meshline.Models.Protocol;

/// <summary>
/// Updates or clears the sender's nickname inside encrypted group content.
/// </summary>
public sealed record GroupMemberNicknameUpdate() : TypedProtocolModel("meshline.group.member.nickname.update")
{
    /// <summary>
    /// The member nickname to assign, or an explicit <see langword="null"/> to clear it.
    /// </summary>
    public required string? Nickname { get; init; }

    /// <summary>
    /// Validates an assigned nickname's text and UTF-8 size, allowing null to clear it.
    /// </summary>
    /// <param name="context">The optional network context passed to nested validation when needed; field-only validation may ignore it.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    public override ProtocolViolation? Validate(NetworkContext? context = null) =>
        Nickname is not null && (string.IsNullOrWhiteSpace(Nickname) || Encoding.UTF8.GetByteCount(Nickname) > 256)
            ? new(ProtocolViolationKind.Format, "A group nickname must contain non-whitespace text and cannot exceed 256 UTF-8 bytes.")
            : null;
}

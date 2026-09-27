using Meshline.Validation;
using System.Collections.Immutable;

namespace Meshline.Models.Protocol;

/// <summary>
/// Combines an admission approval with the client-secret commitment and encrypted member boxes.
/// </summary>
public sealed record GroupApplicationApproveRequest : ProtocolModel
{
    /// <summary>
    /// The signed management operation approving the listed members.
    /// </summary>
    public required GroupApplicationApproval Approval { get; init; }
    /// <summary>
    /// The commitment identifying the group client secret.
    /// </summary>
    public required string ClientSecretCommitment { get; init; }
    /// <summary>
    /// Encrypted client-secret boxes keyed by member account identifier, using ordinal key comparison.
    /// </summary>
    public required ImmutableDictionary<string, GroupSecretBox> ClientSecretBoxes { get; init => field = value.WithComparers(StringComparer.Ordinal); }

    /// <summary>
    /// Validates the approval, client-secret commitment, and one secret box for every approved account.
    /// </summary>
    /// <param name="context">The optional network context passed to nested validation when needed; field-only validation may ignore it.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    public override ProtocolViolation? Validate(NetworkContext? context = null)
    {
        if (Approval.Validate() is { } approvalViolation)
            return approvalViolation;
        var commitment = ClientSecretCommitment.AsSpan();
        if (commitment.Length != 50 || !commitment.StartsWith("sha256:", StringComparison.Ordinal) || !Base64UrlValidator.IsValid(commitment[7..]))
            return new(ProtocolViolationKind.Format, "The client secret commitment must contain 32 canonical base64url-encoded bytes after sha256:.");
        if (ClientSecretBoxes.Count != Approval.Members.Length)
            return new(ProtocolViolationKind.Conflict, "The secret box accounts must exactly match the approved members.");

        foreach (var member in Approval.Members)
        {
            if (!ClientSecretBoxes.TryGetValue(member.Account, out var box))
                return new(ProtocolViolationKind.Conflict, "Every approved member must have a client secret box.");
            if (box is null)
                return new(ProtocolViolationKind.Format, "Client secret boxes cannot contain null.");
            if (box.Validate() is { } boxViolation)
                return boxViolation;
        }
        return null;
    }
}

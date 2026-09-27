using Meshline.Identity;
using Meshline.Validation;
using System.Collections.Immutable;

namespace Meshline.Models.Protocol;

/// <summary>
/// Stages new encrypted client-secret boxes against the current client-secret commitment.
/// </summary>
public sealed record GroupRotationPrepareRequest : ProtocolModel
{
    /// <summary>
    /// The group's canonical <c>grp_</c> identifier.
    /// </summary>
    public required string GroupId { get; init; }
    /// <summary>
    /// The current client-secret commitment against which the rotation is prepared.
    /// </summary>
    public required string BaseCommitment { get; init; }
    /// <summary>
    /// The commitment identifying the group client secret.
    /// </summary>
    public required string ClientSecretCommitment { get; init; }
    /// <summary>
    /// Encrypted client-secret boxes keyed by member account identifier, using ordinal key comparison.
    /// </summary>
    public required ImmutableDictionary<string, GroupSecretBox> ClientSecretBoxes { get; init => field = value.WithComparers(StringComparer.Ordinal); }

    /// <summary>
    /// Validates differing client-secret commitments and the batch of encrypted member boxes.
    /// </summary>
    /// <param name="context">The optional network context passed to nested validation when needed; field-only validation may ignore it.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    public override ProtocolViolation? Validate(NetworkContext? context = null)
    {
        if (Identifiers.ValidateGroupId(GroupId) is { } groupViolation)
            return groupViolation;
        var baseCommitment = BaseCommitment.AsSpan();
        if (baseCommitment.Length != 50 || !baseCommitment.StartsWith("sha256:", StringComparison.Ordinal) || !Base64UrlValidator.IsValid(baseCommitment[7..]))
            return new(ProtocolViolationKind.Format, "The base commitment must contain 32 canonical base64url-encoded bytes after sha256:.");
        var commitment = ClientSecretCommitment.AsSpan();
        if (commitment.Length != 50 || !commitment.StartsWith("sha256:", StringComparison.Ordinal) || !Base64UrlValidator.IsValid(commitment[7..]))
            return new(ProtocolViolationKind.Format, "The client secret commitment must contain 32 canonical base64url-encoded bytes after sha256:.");
        if (BaseCommitment == ClientSecretCommitment)
            return new(ProtocolViolationKind.Conflict, "The new client secret commitment must differ from the base commitment.");
        if (ClientSecretBoxes.IsEmpty)
            return new(ProtocolViolationKind.Format, "A rotation preparation batch must contain at least one client secret box.");

        try
        {
            foreach (var (account, box) in ClientSecretBoxes)
            {
                if (AccountAdapter.ValidateAccountId(account) is { } accountViolation)
                    return accountViolation;
                if (box is null)
                    return new(ProtocolViolationKind.Format, "Client secret boxes cannot contain null.");
                if (box.Validate() is { } boxViolation)
                    return boxViolation;
            }
            return null;
        }
        catch (NotSupportedException exception)
        {
            return new(ProtocolViolationKind.Unsupported, exception.Message);
        }
    }
}

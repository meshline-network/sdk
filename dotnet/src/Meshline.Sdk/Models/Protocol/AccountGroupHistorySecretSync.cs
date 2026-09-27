using Meshline.Validation;
using System.Collections.Immutable;

namespace Meshline.Models.Protocol;

/// <summary>
/// Transfers group history secrets between devices of the same account.
/// </summary>
public sealed record AccountGroupHistorySecretSync() : TypedProtocolModel("meshline.account.group.history_secret.sync")
{
    /// <summary>
    /// The historical group application secrets included in this synchronization message.
    /// </summary>
    public required ImmutableArray<GroupHistorySecret> Secrets { get; init; }

    /// <summary>
    /// Validates historical secret entries and rejects duplicate group-and-epoch pairs.
    /// </summary>
    /// <param name="context">The optional network context passed to nested validation when needed; field-only validation may ignore it.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    public override ProtocolViolation? Validate(NetworkContext? context = null)
    {
        if (Secrets.IsDefaultOrEmpty)
            return new(ProtocolViolationKind.Format, "A history secret synchronization batch must contain at least one secret.");

        var versions = new HashSet<(string GroupId, long Epoch)>();
        foreach (var secret in Secrets)
        {
            if (secret is null)
                return new(ProtocolViolationKind.Format, "History secrets cannot contain null.");
            if (secret.Validate() is { } secretViolation)
                return secretViolation;
            if (!versions.Add((secret.GroupId, secret.Epoch)))
                return new(ProtocolViolationKind.Conflict, "A history secret synchronization batch cannot repeat a group and epoch pair.");
        }
        return null;
    }
}

using Meshline.Validation;
using System.Collections.Immutable;

namespace Meshline.Models.Protocol;

/// <summary>
/// Contains a sequence of group epoch key entries.
/// </summary>
public sealed record GroupKeyPage : ProtocolModel
{
    /// <summary>
    /// The group epoch key entries in this page.
    /// </summary>
    public required ImmutableArray<GroupKeyEntry> Keys { get; init; }
    /// <summary>
    /// Whether additional entries remain beyond this page.
    /// </summary>
    public required bool HasMore { get; init; }

    /// <summary>
    /// Validates increasing epoch order, key-entry structure, and the first entry's client-secret box.
    /// </summary>
    /// <param name="context">The optional network context passed to nested validation when needed; field-only validation may ignore it.</param>
    /// <returns>The first detected protocol violation, or <see langword="null"/> if the implemented checks pass.</returns>
    public override ProtocolViolation? Validate(NetworkContext? context = null)
    {
        if (Keys.IsDefault)
            return new(ProtocolViolationKind.Format, "Group keys must be an initialized array.");
        if (Keys.IsEmpty)
            return HasMore
                ? new(ProtocolViolationKind.Conflict, "An empty group key page cannot have more entries.")
                : null;

        long previousEpoch = -1;
        foreach (var key in Keys)
        {
            if (key is null)
                return new(ProtocolViolationKind.Format, "Group keys cannot contain null.");
            if (key.Validate() is { } keyViolation)
                return keyViolation;
            if (key.Epoch <= previousEpoch)
                return new(ProtocolViolationKind.Conflict, "Group key epochs must be strictly increasing within a page.");
            if (previousEpoch == -1 && key.ClientSecretBox is null)
                return new(ProtocolViolationKind.Format, "The first entry of a group key page must include a client secret box.");
            previousEpoch = key.Epoch;
        }
        return null;
    }
}

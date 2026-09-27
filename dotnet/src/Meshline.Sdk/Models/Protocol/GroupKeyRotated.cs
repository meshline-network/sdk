namespace Meshline.Models.Protocol;

/// <summary>
/// Marks a relay-side group key rotation in the group timeline.
/// </summary>
public sealed record GroupKeyRotated() : TypedProtocolModel("meshline.group.key.rotated")
{
}

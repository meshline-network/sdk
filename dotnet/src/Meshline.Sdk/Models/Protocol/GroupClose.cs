namespace Meshline.Models.Protocol;

/// <summary>
/// Closes a group through its signed management chain.
/// </summary>
public sealed record GroupClose() : GroupManagementOperation("meshline.group.close")
{
}

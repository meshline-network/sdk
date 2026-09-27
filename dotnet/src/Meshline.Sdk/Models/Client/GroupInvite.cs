namespace Meshline.Models.Client;

/// <summary>
/// Contains a signed group invitation associated with its hosting relay.
/// </summary>
public sealed class GroupInvite
{
    /// <summary>
    /// The group and hosting relay associated with this information.
    /// </summary>
    public GroupRef Group { get; }
    /// <summary>
    /// The signed protocol invitation document.
    /// </summary>
    public Protocol.GroupInvite Document { get; }

    /// <summary>
    /// Initializes a new instance of <see cref="GroupInvite"/>.
    /// </summary>
    /// <param name="relayId">The relay's canonical lowercase Neo script-hash identifier.</param>
    /// <param name="document">The signed protocol group invitation to associate with the hosting relay.</param>
    /// <exception cref="ArgumentNullException">The <paramref name="document"/> argument is null.</exception>
    /// <exception cref="ArgumentException">The hosting relay identifier or the invitation document's group identifier is invalid.</exception>
    public GroupInvite(string relayId, Protocol.GroupInvite document)
    {
        ArgumentNullException.ThrowIfNull(document);
        Group = new GroupRef { RelayId = relayId, GroupId = document.GroupId };
        Document = document;
    }
}

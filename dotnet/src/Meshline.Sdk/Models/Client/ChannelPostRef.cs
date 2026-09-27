namespace Meshline.Models.Client;

/// <summary>
/// Identifies a channel post by its channel and original timeline sequence.
/// </summary>
public sealed record ChannelPostRef
{
    /// <summary>
    /// The channel and hosting relay containing the post.
    /// </summary>
    public required ChannelRef Channel { get; init; }

    /// <summary>
    /// The timeline sequence assigned by the hosting relay.
    /// </summary>
    public required long Sequence
    {
        get;
        init
        {
            if (value is < 1 or > 9_007_199_254_740_991) throw new ArgumentOutOfRangeException(nameof(Sequence));
            field = value;
        }
    }
}

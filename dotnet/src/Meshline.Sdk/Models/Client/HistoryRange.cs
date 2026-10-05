namespace Meshline.Models.Client;

/// <summary>
/// Selects adjacent batches of local history using exclusive sequence bounds.
/// </summary>
/// <remarks>
/// Bounds are nonnegative safe integers. If both are supplied, After must be less than Before.
/// With Before, batches move toward older items; otherwise they move forward. Each returned batch is ascending.
/// Query methods copy the bounds when called. Batch size is supplied to the returned reader.
/// </remarks>
public sealed class HistoryRange
{
    /// <summary>The exclusive lower sequence bound, or null for no lower bound.</summary>
    public long? After { get; set; }

    /// <summary>The exclusive upper sequence bound, or null for no upper bound. When supplied, selects the closest earlier batch.</summary>
    public long? Before { get; set; }

    /// <summary>Copies and validates the exclusive sequence bounds for deconstruction.</summary>
    /// <param name="after">The exclusive lower sequence bound, or null for no lower bound.</param>
    /// <param name="before">The exclusive upper sequence bound, or null for no upper bound.</param>
    /// <exception cref="ArgumentOutOfRangeException">A bound is negative or exceeds the maximum safe integer.</exception>
    /// <exception cref="ArgumentException">Both bounds are supplied and After is not less than Before.</exception>
    /// <example><code>var (after, before) = range;</code></example>
    public void Deconstruct(out long? after, out long? before)
    {
        const long maximum = 9_007_199_254_740_991;
        after = After;
        before = Before;
        if (after is < 0 or > maximum) throw new ArgumentOutOfRangeException(nameof(after), "History bounds must be nonnegative safe integers.");
        if (before is < 0 or > maximum) throw new ArgumentOutOfRangeException(nameof(before), "History bounds must be nonnegative safe integers.");
        if (after.HasValue && before.HasValue && after >= before) throw new ArgumentException("After must be less than Before.");
    }
}

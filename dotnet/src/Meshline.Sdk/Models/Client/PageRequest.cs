namespace Meshline.Models.Client;

/// <summary>
/// Specifies an optional page size and continuation cursor for a relay-backed query.
/// </summary>
public sealed class PageRequest
{
    /// <summary>
    /// The positive requested maximum page size, or <see langword="null"/> to use the operation's default.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">An assigned page size is zero or negative.</exception>
    public int? Limit
    {
        get;
        set => field = value is null or > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    /// <summary>
    /// The cursor returned by the preceding page, or <see langword="null"/> for the first page.
    /// </summary>
    public string? Cursor { get; set; }
}

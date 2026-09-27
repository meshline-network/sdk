namespace Meshline.Models.Client;

/// <summary>
/// Contains one page of results and an optional cursor for the next page.
/// </summary>
/// <typeparam name="T">The type of item in the page.</typeparam>
public sealed class Page<T>
{
    /// <summary>
    /// The result entries in this page.
    /// </summary>
    public IReadOnlyList<T> Items { get; }
    /// <summary>
    /// The continuation cursor for the next page, or <see langword="null"/> when no next page is available.
    /// </summary>
    public string? NextCursor { get; }

    internal Page(IReadOnlyList<T> items, string? nextCursor)
    {
        Items = Array.AsReadOnly(items.ToArray());
        NextCursor = nextCursor;
    }
}

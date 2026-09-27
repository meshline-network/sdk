using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Meshline.Models;

/// <summary>
/// Identifies a Meshline network by its Neo network reference and registry contract script hash.
/// </summary>
public sealed partial record NetworkContext : IParsable<NetworkContext>
{
    [GeneratedRegex(@"\A0x[0-9a-f]{40}\z", RegexOptions.CultureInvariant)]
    static partial Regex UInt160Regex { get; }
    [GeneratedRegex(@"\Aneo:(0|[1-9][0-9]{0,9}):(0x[0-9a-f]{40})\z", RegexOptions.CultureInvariant)]
    static partial Regex NetworkContextRegex { get; }

    /// <summary>
    /// The unsigned Neo network reference used in the canonical network identifier.
    /// </summary>
    public required uint Reference { get; init; }
    /// <summary>
    /// The registry contract's lowercase 160-bit script hash, including the <c>0x</c> prefix.
    /// </summary>
    public required string Registry
    {
        get;
        init
        {
            if (!UInt160Regex.IsMatch(value))
                throw new ArgumentException("Expected a lowercase registry script hash.", nameof(value));
            field = value;
        }
    }

    /// <summary>
    /// Parses a canonical <c>neo:reference:registry</c> network identifier.
    /// </summary>
    /// <param name="s">The canonical network identifier text.</param>
    /// <param name="provider">Unused; network identifiers are parsed using invariant rules.</param>
    /// <returns>The network context represented by the input.</returns>
    /// <exception cref="FormatException">The input is not a canonical network identifier.</exception>
    public static NetworkContext Parse(string s, IFormatProvider? provider = null)
    {
        return TryParse(s, provider, out var result)
            ? result
            : throw new FormatException("Expected a canonical Neo network identifier.");
    }

    /// <summary>
    /// Attempts to parse a canonical <c>neo:reference:registry</c> network identifier.
    /// </summary>
    /// <param name="s">The canonical network identifier text.</param>
    /// <param name="result">Receives the parsed context on success, or <see langword="null"/> on failure.</param>
    /// <returns><see langword="true"/> if parsing succeeds; otherwise, <see langword="false"/>.</returns>
    public static bool TryParse(string? s, [NotNullWhen(true)] out NetworkContext? result) =>
        TryParse(s, null, out result);

    /// <summary>
    /// Attempts to parse a canonical <c>neo:reference:registry</c> network identifier.
    /// </summary>
    /// <param name="s">The canonical network identifier text.</param>
    /// <param name="provider">Unused; network identifiers are parsed using invariant rules.</param>
    /// <param name="result">Receives the parsed context on success, or <see langword="null"/> on failure.</param>
    /// <returns><see langword="true"/> if parsing succeeds; otherwise, <see langword="false"/>.</returns>
    public static bool TryParse(string? s, IFormatProvider? provider, [NotNullWhen(true)] out NetworkContext? result)
    {
        result = null;
        if (s is null) return false;

        var match = NetworkContextRegex.Match(s);
        if (!match.Success
            || !uint.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var reference))
            return false;

        result = new NetworkContext
        {
            Reference = reference,
            Registry = match.Groups[2].Value
        };
        return true;
    }

    /// <summary>
    /// Returns the canonical <c>neo:reference:registry</c> network identifier.
    /// </summary>
    /// <returns>The canonical invariant network identifier.</returns>
    public override string ToString() => $"neo:{Reference.ToString(CultureInfo.InvariantCulture)}:{Registry}";
}

using System.Buffers;

namespace Meshline.Validation;

static class Base64UrlValidator
{
    const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
    static readonly SearchValues<char> s_alphabet = SearchValues.Create(Alphabet);

    internal static bool IsValid(ReadOnlySpan<char> value)
    {
        if (value.ContainsAnyExcept(s_alphabet))
            return false;

        return (value.Length % 4) switch
        {
            0 => true,
            2 => (Alphabet.IndexOf(value[^1]) & 15) == 0,
            3 => (Alphabet.IndexOf(value[^1]) & 3) == 0,
            _ => false
        };
    }
}

using System.Text.RegularExpressions;

namespace Meshline.Validation;

static partial class MediaTypeValidator
{
    const string Token = @"[!#$%&'*+.^_`|~0-9A-Za-z-]+";
    const string QuotedString = @"""(?:[\t\x20\x21\x23-\x5B\x5D-\x7E\x80-\xFF]|\\[\t\x20-\x7E\x80-\xFF])*""";

    // RFC 9110 media-type, token, and quoted-string grammar.
    [GeneratedRegex(@"\A" + Token + "/" + Token + @"(?:[ \t]*;[ \t]*(?:(?<name>" + Token + ")=(?<value>" + Token + "|" + QuotedString + @"))?)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex MediaTypeRegex { get; }

    public static bool IsValid(string value, bool requireUtf8Charset = false)
    {
        var match = MediaTypeRegex.Match(value);
        if (!match.Success)
            return false;
        if (!requireUtf8Charset)
            return true;

        var names = match.Groups["name"].Captures;
        var values = match.Groups["value"].Captures;
        for (var i = 0; i < names.Count; i++)
        {
            if (names[i].ValueSpan.Equals("charset", StringComparison.OrdinalIgnoreCase) && !IsUtf8(values[i].ValueSpan))
                return false;
        }
        return true;
    }

    static bool IsUtf8(ReadOnlySpan<char> value)
    {
        if (value[0] == '"')
            value = value[1..^1];
        foreach (var expected in "utf-8")
        {
            if (!value.IsEmpty && value[0] == '\\')
                value = value[1..];
            if (value.IsEmpty || char.ToLowerInvariant(value[0]) != expected)
                return false;
            value = value[1..];
        }
        return value.IsEmpty;
    }
}

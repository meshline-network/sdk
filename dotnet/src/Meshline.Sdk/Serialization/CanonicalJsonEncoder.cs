using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;

namespace Meshline.Serialization;

sealed class CanonicalJsonEncoder : JavaScriptEncoder
{
    public static CanonicalJsonEncoder Instance { get; } = new();

    public override int MaxOutputCharactersPerInputCharacter => 6;

    CanonicalJsonEncoder() { }

    public override bool WillEncode(int unicodeScalar) => !Rune.IsValid(unicodeScalar) || unicodeScalar is < 0x20 or '"' or '\\';

    public override unsafe int FindFirstCharacterToEncode(char* text, int textLength)
    {
        var source = new ReadOnlySpan<char>(text, textLength);
        for (var index = 0; index < source.Length;)
        {
            if (Rune.DecodeFromUtf16(source[index..], out var rune, out var consumed) != OperationStatus.Done || WillEncode(rune.Value))
                return index;

            index += consumed;
        }
        return -1;
    }

    public override unsafe bool TryEncodeUnicodeScalar(int unicodeScalar, char* buffer, int bufferLength, out int numberOfCharactersWritten)
    {
        const string hex = "0123456789abcdef";
        var rune = new Rune(unicodeScalar);
        var shortEscape = unicodeScalar switch
        {
            '"' => '"',
            '\\' => '\\',
            '\b' => 'b',
            '\t' => 't',
            '\n' => 'n',
            '\f' => 'f',
            '\r' => 'r',
            _ => '\0'
        };
        var length = shortEscape != '\0' ? 2 : unicodeScalar < 0x20 ? 6 : rune.Utf16SequenceLength;
        numberOfCharactersWritten = 0;
        if (bufferLength < length)
            return false;

        var destination = new Span<char>(buffer, bufferLength);
        if (shortEscape != '\0')
        {
            destination[0] = '\\';
            destination[1] = shortEscape;
        }
        else if (unicodeScalar < 0x20)
        {
            destination[0] = '\\';
            destination[1] = 'u';
            destination[2] = '0';
            destination[3] = '0';
            destination[4] = hex[unicodeScalar >> 4];
            destination[5] = hex[unicodeScalar & 0x0f];
        }
        else
        {
            rune.EncodeToUtf16(destination);
        }

        numberOfCharactersWritten = length;
        return true;
    }
}

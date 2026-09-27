using System.Text.Json;

namespace Meshline.Validation;

static class ProtocolStringValidator
{
    public static string Validate(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (!char.IsSurrogate(value[i]))
                continue;

            if (i + 1 >= value.Length || !char.IsSurrogatePair(value[i], value[i + 1]))
                throw new JsonException("JSON strings must contain only Unicode scalar values.");

            i++;
        }

        return value;
    }
}

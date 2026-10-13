using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;

namespace DbEmulator.Storage;

/// <summary>
/// Escapes only what JSON requires (quote, backslash, control characters, invalid UTF-16/UTF-8). The built-in
/// encoders also escape characters outside the Basic Multilingual Plane (😀 becomes 😀); this one keeps
/// text exactly as Cosmos returns it, so responses and backups stay byte-for-byte faithful and readable.
/// </summary>
internal sealed class MinimalJsonEncoder : JavaScriptEncoder
{
    public static readonly MinimalJsonEncoder Instance = new();

    public override int MaxOutputCharactersPerInputCharacter => 6;

    public override bool WillEncode(int unicodeScalar) =>
        unicodeScalar < 0x20 || unicodeScalar == '"' || unicodeScalar == '\\' || unicodeScalar is >= 0xD800 and <= 0xDFFF;

    public override unsafe int FindFirstCharacterToEncode(char* text, int textLength)
    {
        for (var i = 0; i < textLength; i++)
        {
            var c = text[i];
            if (c < 0x20 || c == '"' || c == '\\')
            {
                return i;
            }

            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < textLength && char.IsLowSurrogate(text[i + 1]))
                {
                    i++;
                    continue;
                }

                return i;
            }

            if (char.IsLowSurrogate(c))
            {
                return i;
            }
        }

        return -1;
    }

    public override int FindFirstCharacterToEncodeUtf8(ReadOnlySpan<byte> utf8Text)
    {
        var i = 0;
        while (i < utf8Text.Length)
        {
            var b = utf8Text[i];
            if (b < 0x80)
            {
                if (b < 0x20 || b == '"' || b == '\\')
                {
                    return i;
                }

                i++;
                continue;
            }

            if (Rune.DecodeFromUtf8(utf8Text[i..], out _, out var consumed) != OperationStatus.Done)
            {
                return i;
            }

            i += consumed;
        }

        return -1;
    }

    public override unsafe bool TryEncodeUnicodeScalar(int unicodeScalar, char* buffer, int bufferLength, out int numberOfCharactersWritten)
    {
        var escaped = unicodeScalar switch
        {
            '"' => "\\\"",
            '\\' => "\\\\",
            '\n' => "\\n",
            '\r' => "\\r",
            '\t' => "\\t",
            '\b' => "\\b",
            '\f' => "\\f",
            _ when WillEncode(unicodeScalar) => "\\u" + unicodeScalar.ToString("X4", CultureInfo.InvariantCulture),
            _ => null,
        };

        if (escaped is null)
        {
            // Not escaped: write the character itself.
            var rune = new Rune(unicodeScalar);
            if (rune.Utf16SequenceLength > bufferLength)
            {
                numberOfCharactersWritten = 0;
                return false;
            }

            numberOfCharactersWritten = rune.EncodeToUtf16(new Span<char>(buffer, bufferLength));
            return true;
        }

        if (escaped.Length > bufferLength)
        {
            numberOfCharactersWritten = 0;
            return false;
        }

        for (var i = 0; i < escaped.Length; i++)
        {
            buffer[i] = escaped[i];
        }

        numberOfCharactersWritten = escaped.Length;
        return true;
    }
}

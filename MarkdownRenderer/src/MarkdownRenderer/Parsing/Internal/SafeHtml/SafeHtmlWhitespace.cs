using System.Net;

namespace MarkdownRenderer.Parsing.Internal.SafeHtml;

/// <summary>Collapses HTML whitespace runs the way rendered inline text does.</summary>
internal static class SafeHtmlWhitespace
{
    internal static string CollapseWhitespace(string? text) =>
        CollapseDecodedWhitespace(WebUtility.HtmlDecode(text ?? string.Empty));

    // Attribute values and SafeHtmlText.DecodedText have already crossed the
    // HTML decoding boundary. Whitespace normalization must not reinterpret
    // literal entity-like content in those values.
    internal static string CollapseDecodedWhitespace(string? decodedText)
    {
        string value = decodedText ?? string.Empty;
        if (value.Length == 0)
        {
            return string.Empty;
        }

        char[] buffer = new char[value.Length];
        int written = 0;
        bool pendingWhitespace = false;
        foreach (char character in value)
        {
            if (character == '\u00A0')
            {
                if (pendingWhitespace && written > 0)
                {
                    buffer[written++] = ' ';
                }

                pendingWhitespace = false;
                buffer[written++] = character;
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                pendingWhitespace = true;
                continue;
            }

            if (pendingWhitespace && written > 0)
            {
                buffer[written++] = ' ';
            }

            pendingWhitespace = false;
            buffer[written++] = character;
        }

        return new string(buffer, 0, written);
    }
}

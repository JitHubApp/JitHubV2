using System;
using System.Text;

namespace MarkdownRenderer.Parsing;

/// <summary>
/// Pre-parse fixup for image-link destinations that contain a
/// <c>data:</c> URI with literal characters CommonMark would otherwise
/// truncate at — spaces, <c>&lt;</c>, and <c>&gt;</c>. Markdig (faithfully
/// implementing CommonMark) stops the destination at the first space
/// unless it's wrapped in <c>&lt;...&gt;</c>, and angle-bracket
/// destinations themselves can't contain raw <c>&gt;</c> — so a literal
/// inline SVG payload like
/// <code>![x](data:image/svg+xml;utf8,&lt;svg xmlns="..." /&gt;)</code>
/// would otherwise yield a truncated URL of
/// <c>data:image/svg+xml;utf8,&lt;svg</c>.
///
/// This fixup walks the source, finds <c>](data:</c> openings, balances
/// the surrounding parentheses (so payloads with their own <c>(</c>/<c>)</c>
/// stay paired), and percent-encodes the payload's spaces, angle
/// brackets, and unbalanced parens. The result remains a valid CommonMark
/// link destination, and <c>Uri.UnescapeDataString</c> in
/// <c>ImageBox.LoadSvgDataUriAsync</c> reverses the encoding before
/// rasterization. The transformation is a no-op on already-well-formed
/// markdown, including base64 data URIs and angle-bracket-wrapped ones.
/// </summary>
public static class ForgivingDataUriFixer
{
    /// <summary>
    /// Returns a markdown string with malformed inline data-URI image
    /// destinations rewritten so Markdig's CommonMark parser preserves
    /// the entire payload. Always safe to call; cheap when no <c>](data:</c>
    /// substring is present.
    /// </summary>
    public static string Fix(string source)
    {
        if (string.IsNullOrEmpty(source)) return source ?? string.Empty;
        // Cheap reject: no opportunity if the trigger sequence is absent.
        if (source.IndexOf("](data:", StringComparison.Ordinal) < 0) return source;

        var sb = new StringBuilder(source.Length + 32);
        int i = 0;
        while (i < source.Length)
        {
            int hit = source.IndexOf("](data:", i, StringComparison.Ordinal);
            if (hit < 0)
            {
                sb.Append(source, i, source.Length - i);
                break;
            }
            // Copy everything up to and including the `](`.
            sb.Append(source, i, hit + 2 - i);
            int payloadStart = hit + 2;
            // Skip an existing angle-bracket wrapper — already CommonMark-safe
            // for spaces. (It still can't contain `>` but author opted in.)
            if (payloadStart < source.Length && source[payloadStart] == '<')
            {
                i = payloadStart;
                continue;
            }
            // Locate the matching `)` accounting for nested parens.
            int depth = 1;
            int end = payloadStart;
            while (end < source.Length)
            {
                char c = source[end];
                if (c == '(') depth++;
                else if (c == ')')
                {
                    depth--;
                    if (depth == 0) break;
                }
                else if (c == '\n')
                {
                    // CommonMark links may not span hard line breaks; bail.
                    break;
                }
                end++;
            }
            if (end >= source.Length || source[end] != ')')
            {
                // Unbalanced — leave the rest of the input untouched and let
                // Markdig do whatever it normally would.
                sb.Append(source, payloadStart, source.Length - payloadStart);
                i = source.Length;
                break;
            }
            string payload = source.Substring(payloadStart, end - payloadStart);
            string encoded = EncodePayload(payload);
            sb.Append(encoded);
            sb.Append(')');
            i = end + 1;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Percent-encodes the four characters that break CommonMark link
    /// destinations (<c>space</c>, <c>&lt;</c>, <c>&gt;</c>, raw quote)
    /// while leaving everything else — including data-URI mandatory
    /// punctuation like <c>:</c>, <c>;</c>, <c>,</c>, <c>=</c>, <c>/</c>,
    /// <c>+</c> — alone. Uses uppercase hex per RFC 3986.
    /// </summary>
    private static string EncodePayload(string payload)
    {
        var sb = new StringBuilder(payload.Length);
        foreach (char c in payload)
        {
            switch (c)
            {
                case ' ': sb.Append("%20"); break;
                case '<': sb.Append("%3C"); break;
                case '>': sb.Append("%3E"); break;
                case '"': sb.Append("%22"); break;
                case '\t': sb.Append("%09"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }
}

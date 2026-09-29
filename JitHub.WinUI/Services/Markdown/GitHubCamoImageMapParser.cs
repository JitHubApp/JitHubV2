using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace JitHub.Services.Markdown;

internal static partial class GitHubCamoImageMapParser
{
    private const int MaximumEncodedOriginLength = 32 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static IReadOnlyDictionary<string, string> Parse(string? html)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match image in ImageTagRegex().Matches(html ?? string.Empty))
        {
            var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match attribute in HtmlAttributeRegex().Matches(image.Value))
            {
                Group doubleQuoted = attribute.Groups["double"];
                attributes[attribute.Groups["name"].Value] = doubleQuoted.Success
                    ? doubleQuoted.Value
                    : attribute.Groups["single"].Value;
            }

            if (!attributes.TryGetValue("src", out string? encodedCamo) ||
                !attributes.TryGetValue("data-canonical-src", out string? encodedOriginal))
            {
                continue;
            }

            string original = WebUtility.HtmlDecode(encodedOriginal);
            string camo = WebUtility.HtmlDecode(encodedCamo);
            if (Uri.TryCreate(original, UriKind.Absolute, out Uri? originalUri) &&
                (originalUri.Scheme == Uri.UriSchemeHttp ||
                    originalUri.Scheme == Uri.UriSchemeHttps) &&
                Uri.TryCreate(camo, UriKind.Absolute, out Uri? camoUri) &&
                camoUri.Scheme == Uri.UriSchemeHttps &&
                camoUri.Host.Equals("camo.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
            {
                result[originalUri.AbsoluteUri] = camoUri.AbsoluteUri;
            }
        }

        return result;
    }

    /// <summary>
    /// Recovers the canonical origin encoded in GitHub's Camo path. Legacy
    /// HTTP origins are upgraded to HTTPS; plaintext fallback is never used.
    /// This is used only as a bounded fallback when the trusted Camo endpoint
    /// stalls or fails; the recovered URL still goes through the host's remote-image
    /// policy and an anonymous third-party fetch scope.
    /// </summary>
    internal static bool TryDecodeCanonicalSource(Uri? camoUri, out Uri sourceUri)
    {
        sourceUri = null!;
        if (camoUri is null ||
            camoUri.Scheme != Uri.UriSchemeHttps ||
            !camoUri.Host.Equals("camo.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
            !camoUri.IsDefaultPort ||
            camoUri.UserInfo.Length != 0 ||
            camoUri.Query.Length != 0 ||
            camoUri.Fragment.Length != 0)
        {
            return false;
        }

        string[] segments = camoUri.AbsolutePath.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2 ||
            segments[0].Length is not (40 or 64) ||
            !IsAsciiHex(segments[0]) ||
            segments[1].Length is 0 or > MaximumEncodedOriginLength ||
            (segments[1].Length & 1) != 0 ||
            !IsAsciiHex(segments[1]))
        {
            return false;
        }

        try
        {
            string decoded = StrictUtf8.GetString(Convert.FromHexString(segments[1]));
            if (!Uri.TryCreate(decoded, UriKind.Absolute, out Uri? decodedUri) ||
                decodedUri.UserInfo.Length != 0 ||
                (!decodedUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                    !decodedUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            if (decodedUri.Scheme == Uri.UriSchemeHttp)
            {
                var upgraded = new UriBuilder(decodedUri)
                {
                    Scheme = Uri.UriSchemeHttps,
                    Port = -1,
                };
                sourceUri = upgraded.Uri;
            }
            else
            {
                sourceUri = decodedUri;
            }

            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool IsAsciiHex(string value)
    {
        foreach (char character in value)
        {
            if (character is not (>= '0' and <= '9') and
                not (>= 'a' and <= 'f') and
                not (>= 'A' and <= 'F'))
            {
                return false;
            }
        }

        return true;
    }

    [GeneratedRegex(
        @"<img\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex ImageTagRegex();

    [GeneratedRegex(
        @"(?<name>[A-Za-z_:][A-Za-z0-9_.:-]*)\s*=\s*(?:""(?<double>[^""]*)""|'(?<single>[^']*)')",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex HtmlAttributeRegex();
}

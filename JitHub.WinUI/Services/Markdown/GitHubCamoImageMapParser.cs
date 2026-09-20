using System;
using System.Collections.Generic;
using System.Net;
using System.Text.RegularExpressions;

namespace JitHub.Services.Markdown;

internal static partial class GitHubCamoImageMapParser
{
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

    [GeneratedRegex(
        @"<img\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex ImageTagRegex();

    [GeneratedRegex(
        @"(?<name>[A-Za-z_:][A-Za-z0-9_.:-]*)\s*=\s*(?:""(?<double>[^""]*)""|'(?<single>[^']*)')",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex HtmlAttributeRegex();
}

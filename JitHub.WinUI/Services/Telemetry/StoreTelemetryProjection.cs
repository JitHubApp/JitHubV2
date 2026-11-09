using System;
using System.Collections.Generic;

namespace JitHub.Services;

/// <summary>
/// Projects a few sanitized categorical dimensions into a second, bounded
/// Partner Center event name. The Store engagement API has no property bag.
/// </summary>
internal static class StoreTelemetryProjection
{
    private const string Prefix = "p.";
    private const int MaxDimensions = 4;
    private const int MaxEventNameLength = 120;

    private static readonly (string Property, string Code)[] OrderedDimensions =
    [
        ("action", "a"),
        ("result", "r"),
        ("error_kind", "e"),
        ("feature", "ft"),
        ("section", "s"),
        ("filter_type", "f"),
        ("cache_state", "c"),
        ("page", "p"),
        ("widget", "w"),
        ("sort", "o"),
        ("view_mode", "v"),
        ("theme_palette", "tp"),
        ("source", "src"),
        ("phase", "ph"),
        ("policy", "po"),
        ("priority", "pr"),
        ("query_kind", "q"),
        ("resource", "u"),
        ("status", "st"),
        ("duration_bucket", "d"),
        ("count_bucket", "n"),
        ("http_status", "h"),
        ("is_background", "b"),
        ("refresh", "rf"),
        ("event_kind", "k")
    ];

    private static readonly IReadOnlyDictionary<string, string> PropertyByCode =
        CreatePropertyByCode();

    public static string? Create(
        string eventName,
        IReadOnlyDictionary<string, string> sanitizedProperties)
    {
        if (!TelemetrySanitizer.IsBaseStoreEventAllowed(eventName) || sanitizedProperties.Count == 0)
        {
            return null;
        }

        string projected = Prefix + eventName;
        int count = 0;
        foreach ((string property, string code) in OrderedDimensions)
        {
            if (count >= MaxDimensions ||
                !sanitizedProperties.TryGetValue(property, out string? value) ||
                string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            string candidate = $"{projected}.{code}.{value}";
            if (candidate.Length > MaxEventNameLength)
            {
                continue;
            }

            projected = candidate;
            count++;
        }

        return count == 0 ? null : projected;
    }

    public static bool IsAllowed(string name)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            name.Length > MaxEventNameLength ||
            !name.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (string baseEvent in TelemetrySanitizer.GetAllowedStoreEvents())
        {
            string eventPrefix = Prefix + baseEvent + ".";
            if (!name.StartsWith(eventPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            string[] segments = name[eventPrefix.Length..].Split('.', StringSplitOptions.None);
            if (segments.Length is < 2 or > MaxDimensions * 2 || segments.Length % 2 != 0)
            {
                return false;
            }

            HashSet<string> seenCodes = new(StringComparer.Ordinal);
            for (int index = 0; index < segments.Length; index += 2)
            {
                string code = segments[index];
                string value = segments[index + 1];
                if (!seenCodes.Add(code) ||
                    !PropertyByCode.TryGetValue(code, out string? property) ||
                    !IsSanitizedValue(property, value))
                {
                    return false;
                }
            }

            return true;
        }

        return false;
    }

    private static bool IsSanitizedValue(string property, string value)
    {
        IReadOnlyDictionary<string, string> sanitized = TelemetrySanitizer.SanitizeProperties(
            new Dictionary<string, string?> { [property] = value });
        return sanitized.TryGetValue(property, out string? accepted) &&
            string.Equals(accepted, value, StringComparison.Ordinal);
    }

    private static IReadOnlyDictionary<string, string> CreatePropertyByCode()
    {
        Dictionary<string, string> properties = new(StringComparer.Ordinal);
        foreach ((string property, string code) in OrderedDimensions)
        {
            properties.Add(code, property);
        }

        return properties;
    }
}

using System.Globalization;
using System.Text.Json.Nodes;

namespace MarkdownRenderer.Tests.Engineering.PerformanceGates;

/// <summary>Small JSON edits shared by the gate fixture mutation tables.</summary>
internal static class PerformanceGateJson
{
    /// <summary>Adds one to a floating-point property.</summary>
    internal static void AddOne(JsonObject owner, string propertyName)
        => owner[propertyName] = owner[propertyName]!.GetValue<double>() + 1;

    /// <summary>Parses an ISO 8601 timestamp property.</summary>
    internal static DateTimeOffset ParseTimestamp(JsonNode timestamp)
        => DateTimeOffset.Parse(timestamp.GetValue<string>(), CultureInfo.InvariantCulture);

    /// <summary>
    /// Returns the same instant as <paramref name="timestamp"/> written with a
    /// -07:00 offset, which the gate must reject as non-UTC.
    /// </summary>
    internal static string ToNonUtcTimestamp(JsonNode timestamp)
        => ParseTimestamp(timestamp)
            .ToOffset(TimeSpan.FromHours(-7))
            .ToString("O", CultureInfo.InvariantCulture);
}

using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace JitHub.Services;

public static class PullRequestReviewAutomationIdentity
{
    public static string CreateScope(
        string prefix,
        long id,
        string? nodeId,
        long? reviewId,
        int? position,
        int? originalPosition,
        DateTimeOffset createdAt,
        string? deterministicContext = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        if (id > 0)
        {
            return $"{Sanitize(prefix, 36)}_{id.ToString(CultureInfo.InvariantCulture)}";
        }

        bool hasStableCoordinates = reviewId.HasValue || position.HasValue || originalPosition.HasValue ||
            createdAt != default;
        bool usesDeterministicContext = string.IsNullOrWhiteSpace(nodeId) && !hasStableCoordinates;
        if (usesDeterministicContext && string.IsNullOrWhiteSpace(deterministicContext))
        {
            throw new ArgumentException(
                "A deterministic owner/ordinal context is required when GitHub supplies no review identity.",
                nameof(deterministicContext));
        }

        string stableScope = !string.IsNullOrWhiteSpace(nodeId)
            ? nodeId.Trim()
            : hasStableCoordinates
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $"{reviewId ?? 0}:{position ?? originalPosition ?? 0}:{createdAt.UtcTicks}")
                : $"context:{deterministicContext!.Trim()}";
        // Context keys are structural UI coordinates. Keep their raw value out of the UIA tree.
        string readableScope = usesDeterministicContext ? "context" : Sanitize(stableScope, 36);
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stableScope)))[..12];
        return $"{Sanitize(prefix, 36)}_{readableScope}_{digest}";
    }

    private static string Sanitize(string value, int maximumLength)
    {
        StringBuilder result = new(Math.Min(value.Length, maximumLength));
        foreach (char character in value)
        {
            if (result.Length == maximumLength)
            {
                break;
            }

            if (char.IsLetterOrDigit(character))
            {
                result.Append(character);
            }
            else if (result.Length > 0 && result[^1] != '_')
            {
                result.Append('_');
            }
        }

        return result.Length == 0 ? "item" : result.ToString().TrimEnd('_');
    }
}

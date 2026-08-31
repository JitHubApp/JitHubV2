using System;

namespace JitHub.Services;

internal readonly record struct DialogMutationOutcome(bool Succeeded, string? ErrorMessage);

internal static class DialogMutationOutcomePolicy
{
    private static readonly string[] ExplicitFailurePhrases =
    [
        "reviewer changes failed",
        "could not update reviewers",
        "did not merge",
        "could not reach GitHub"
    ];

    public static DialogMutationOutcome Resolve(
        string? previousStatus,
        string? currentStatus,
        bool observableSuccess,
        string successText,
        string fallbackError)
    {
        string current = currentStatus?.Trim() ?? string.Empty;
        foreach (string phrase in ExplicitFailurePhrases)
        {
            if (current.Contains(phrase, StringComparison.OrdinalIgnoreCase))
            {
                return new(false, current);
            }
        }

        if (observableSuccess || current.Contains(successText, StringComparison.OrdinalIgnoreCase))
        {
            return new(true, null);
        }

        return new(
            false,
            !string.Equals(previousStatus, current, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(current)
                ? current
                : fallbackError);
    }
}

namespace JitHub.Services;

internal static class PullRequestProgressiveSelectionPolicy
{
    public static int ResolvePreferredNumber(
        long loadSelectionGeneration,
        long currentSelectionGeneration,
        int requestedPreferredNumber,
        int? currentSelectedNumber)
    {
        if (loadSelectionGeneration == currentSelectionGeneration)
        {
            return requestedPreferredNumber;
        }

        // A selection made after this load began owns the remainder of the
        // progressive publication. -1 means the user deliberately cleared it;
        // zero remains reserved for the normal first-row fallback.
        return currentSelectedNumber is > 0 ? currentSelectedNumber.Value : -1;
    }
}

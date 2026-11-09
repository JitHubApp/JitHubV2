using System.Globalization;

namespace JitHub.WinUI.ViewModels.Common;

public static class RepositoryActionPresentation
{
    public static string StarLabel(RepositoryDataAvailability state, bool isStarred) => state switch
    {
        RepositoryDataAvailability.Loading => "Loading star status",
        RepositoryDataAvailability.Unavailable => "Star status unavailable",
        _ => isStarred ? "Unstar repository" : "Star repository"
    };

    public static string WatchLabel(RepositoryDataAvailability state, bool isWatching) => state switch
    {
        RepositoryDataAvailability.Loading => "Loading watch status",
        RepositoryDataAvailability.Unavailable => "Watch status unavailable",
        _ => isWatching ? "Unwatch repository" : "Watch repository"
    };

    public static string ValueText(RepositoryDataAvailability state, int count) => state switch
    {
        RepositoryDataAvailability.Loading => "...",
        RepositoryDataAvailability.Unavailable => "N/A",
        _ => FormatCompactCount(count)
    };

    public static string BranchStatus(RepositoryDataAvailability state, int branchCount) => state switch
    {
        RepositoryDataAvailability.Loading => "Loading branches",
        RepositoryDataAvailability.Unavailable => "Branches unavailable",
        _ when branchCount == 0 => "No branches available",
        _ => string.Empty
    };

    private static string FormatCompactCount(int value)
    {
        if (value >= 1_000_000)
        {
            return $"{value / 1_000_000d:0.#}m";
        }

        return value >= 1_000
            ? $"{value / 1_000d:0.#}k"
            : value.ToString(CultureInfo.CurrentCulture);
    }
}

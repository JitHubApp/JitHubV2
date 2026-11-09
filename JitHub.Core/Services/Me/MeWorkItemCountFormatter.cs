using System.Globalization;

namespace JitHub.Services;

public sealed record MeWorkItemCountFormats(
    string Loading,
    string LoadingOfTotal,
    string Partial,
    string PartialOfTotal,
    string ApiLimited,
    string ApiLimitedOfTotal)
{
    public static MeWorkItemCountFormats English { get; } = new(
        "{0} loading",
        "{0} of {1} loading",
        "{0} loaded (partial)",
        "{0} of {1} loaded (partial)",
        "{0} indexed (GitHub API limit)",
        "{0} of {1} indexed (GitHub API limit)");
}

public static class MeWorkItemCountFormatter
{
    public static string Format(int loadedCount, int reportedTotalCount, int searchResultLimit)
    {
        int loaded = System.Math.Max(0, loadedCount);
        int reported = System.Math.Max(0, reportedTotalCount);
        int limit = System.Math.Max(1, searchResultLimit);
        if (reported > limit && loaded >= limit)
        {
            return $"{loaded.ToString("N0", CultureInfo.CurrentCulture)} indexed";
        }

        if (loaded < reported)
        {
            return $"{loaded.ToString("N0", CultureInfo.CurrentCulture)} loaded";
        }

        return loaded.ToString("N0", CultureInfo.CurrentCulture);
    }

    public static string Format(
        int loadedCount,
        int reportedTotalCount,
        int searchResultLimit,
        PagedDataCompleteness completeness,
        MeWorkItemCountFormats? formats = null)
    {
        formats ??= MeWorkItemCountFormats.English;
        int loaded = System.Math.Max(0, loadedCount);
        int reported = System.Math.Max(loaded, reportedTotalCount);
        int limit = System.Math.Max(1, searchResultLimit);
        string loadedText = loaded.ToString("N0", CultureInfo.CurrentCulture);
        string reportedText = reported.ToString("N0", CultureInfo.CurrentCulture);

        return completeness switch
        {
            PagedDataCompleteness.ApiLimited => reported > loaded
                ? string.Format(
                    CultureInfo.CurrentCulture,
                    formats.ApiLimitedOfTotal,
                    loadedText,
                    reportedText)
                : string.Format(
                    CultureInfo.CurrentCulture,
                    formats.ApiLimited,
                    loadedText),
            PagedDataCompleteness.Partial => reported > loaded
                ? string.Format(
                    CultureInfo.CurrentCulture,
                    formats.PartialOfTotal,
                    loadedText,
                    reportedText)
                : string.Format(
                    CultureInfo.CurrentCulture,
                    formats.Partial,
                    loadedText),
            PagedDataCompleteness.Loading => reported > loaded
                ? string.Format(
                    CultureInfo.CurrentCulture,
                    formats.LoadingOfTotal,
                    loadedText,
                    reportedText)
                : string.Format(
                    CultureInfo.CurrentCulture,
                    formats.Loading,
                    loadedText),
            _ when reported > limit && loaded >= limit => string.Format(
                CultureInfo.CurrentCulture,
                formats.ApiLimited,
                loadedText),
            _ => loadedText
        };
    }
}

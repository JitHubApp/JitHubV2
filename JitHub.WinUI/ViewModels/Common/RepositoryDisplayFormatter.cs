using System;
using System.Globalization;

namespace JitHub.WinUI.ViewModels.Common;

public static class RepositoryDisplayFormatter
{
    public static string FormatCount(int value)
    {
        if (value >= 1_000_000)
        {
            return $"{value / 1_000_000d:0.#}m";
        }

        return value >= 1_000
            ? $"{value / 1_000d:0.#}k"
            : value.ToString(CultureInfo.InvariantCulture);
    }

    public static string FormatRelativeUpdate(DateTimeOffset? value)
    {
        if (value is null)
        {
            return "Updated recently";
        }

        TimeSpan age = DateTimeOffset.Now - value.Value.ToLocalTime();
        if (age.TotalMinutes < 1)
        {
            return "Updated just now";
        }

        if (age.TotalHours < 1)
        {
            return $"Updated {(int)Math.Max(1, age.TotalMinutes)}m ago";
        }

        if (age.TotalDays < 1)
        {
            return $"Updated {(int)Math.Max(1, age.TotalHours)}h ago";
        }

        return age.TotalDays < 30
            ? $"Updated {(int)Math.Max(1, age.TotalDays)}d ago"
            : value.Value.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.CurrentCulture);
    }
}

using System;
using System.Collections.Generic;

namespace JitHub.WinUI.Views.Controls.Profile;

internal enum ProfileContributionNavigationDirection
{
    PreviousWeek,
    NextWeek,
    PreviousDay,
    NextDay,
    FirstDay,
    LastDay
}

internal readonly record struct ProfileContributionCursor(int WeekIndex, int DayIndex);

internal readonly record struct ProfileContributionLayoutMetrics(double CellSize, double Gap)
{
    public double WidthFor(int weekCount) =>
        (CellSize * weekCount) + (Gap * Math.Max(0, weekCount - 1));
}

internal static class ProfileContributionGraphNavigation
{
    public static ProfileContributionLayoutMetrics CalculateLayout(double availableWidth, int weekCount)
    {
        if (availableWidth <= 0 || weekCount <= 0)
        {
            return new ProfileContributionLayoutMetrics(0, 0);
        }

        double preferredGap = availableWidth >= 520 ? 3 : availableWidth >= 360 ? 2 : 1;
        double maximumFittingGap = weekCount > 1
            ? Math.Max(0, (availableWidth - Math.Min(availableWidth, weekCount)) / (weekCount - 1))
            : preferredGap;
        double gap = Math.Min(preferredGap, maximumFittingGap);
        double rawCellSize = (availableWidth - (Math.Max(0, weekCount - 1) * gap)) / weekCount;
        double cellSize = Math.Min(11, Math.Floor(rawCellSize));
        if (cellSize < 1)
        {
            cellSize = rawCellSize;
        }

        return new ProfileContributionLayoutMetrics(cellSize, gap);
    }

    public static ProfileContributionCursor Move(
        ProfileContributionCursor current,
        IReadOnlyList<int> weekDayCounts,
        ProfileContributionNavigationDirection direction)
    {
        ArgumentNullException.ThrowIfNull(weekDayCounts);

        ProfileContributionCursor first = FindFirst(weekDayCounts);
        if (first.WeekIndex < 0)
        {
            return first;
        }

        ProfileContributionCursor normalized = Normalize(current, weekDayCounts, first);
        return direction switch
        {
            ProfileContributionNavigationDirection.PreviousWeek => MoveWeek(normalized, weekDayCounts, -1),
            ProfileContributionNavigationDirection.NextWeek => MoveWeek(normalized, weekDayCounts, 1),
            ProfileContributionNavigationDirection.PreviousDay => MoveDay(normalized, weekDayCounts, -1),
            ProfileContributionNavigationDirection.NextDay => MoveDay(normalized, weekDayCounts, 1),
            ProfileContributionNavigationDirection.FirstDay => first,
            ProfileContributionNavigationDirection.LastDay => FindLast(weekDayCounts),
            _ => normalized
        };
    }

    public static ProfileContributionCursor FindLast(IReadOnlyList<int> weekDayCounts)
    {
        ArgumentNullException.ThrowIfNull(weekDayCounts);

        for (int week = weekDayCounts.Count - 1; week >= 0; week--)
        {
            if (weekDayCounts[week] > 0)
            {
                return new ProfileContributionCursor(week, weekDayCounts[week] - 1);
            }
        }

        return new ProfileContributionCursor(-1, -1);
    }

    private static ProfileContributionCursor FindFirst(IReadOnlyList<int> weekDayCounts)
    {
        for (int week = 0; week < weekDayCounts.Count; week++)
        {
            if (weekDayCounts[week] > 0)
            {
                return new ProfileContributionCursor(week, 0);
            }
        }

        return new ProfileContributionCursor(-1, -1);
    }

    private static ProfileContributionCursor Normalize(
        ProfileContributionCursor current,
        IReadOnlyList<int> weekDayCounts,
        ProfileContributionCursor fallback)
    {
        if (current.WeekIndex < 0
            || current.WeekIndex >= weekDayCounts.Count
            || weekDayCounts[current.WeekIndex] <= 0)
        {
            return fallback;
        }

        return new ProfileContributionCursor(
            current.WeekIndex,
            Math.Clamp(current.DayIndex, 0, weekDayCounts[current.WeekIndex] - 1));
    }

    private static ProfileContributionCursor MoveWeek(
        ProfileContributionCursor current,
        IReadOnlyList<int> weekDayCounts,
        int offset)
    {
        for (int week = current.WeekIndex + offset;
             week >= 0 && week < weekDayCounts.Count;
             week += offset)
        {
            if (weekDayCounts[week] > 0)
            {
                return new ProfileContributionCursor(
                    week,
                    Math.Min(current.DayIndex, weekDayCounts[week] - 1));
            }
        }

        return current;
    }

    private static ProfileContributionCursor MoveDay(
        ProfileContributionCursor current,
        IReadOnlyList<int> weekDayCounts,
        int offset)
    {
        int day = current.DayIndex + offset;
        if (day >= 0 && day < weekDayCounts[current.WeekIndex])
        {
            return new ProfileContributionCursor(current.WeekIndex, day);
        }

        return current;
    }
}

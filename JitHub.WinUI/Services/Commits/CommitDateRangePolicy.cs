using System;

namespace JitHub.Services;

public static class CommitDateRangePolicy
{
    public static DateTimeOffset? StartOfDay(DateTimeOffset? value) => value is DateTimeOffset date
        ? new DateTimeOffset(date.Year, date.Month, date.Day, 0, 0, 0, date.Offset)
        : null;

    public static DateTimeOffset? EndOfDay(DateTimeOffset? value) =>
        StartOfDay(value)?.AddDays(1).AddTicks(-1);
}

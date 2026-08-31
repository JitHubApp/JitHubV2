using System;
using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class CommitDateRangePolicyTests
{
    [Fact]
    public void SelectedDatesCoverTheCompleteLocalCalendarDay()
    {
        DateTimeOffset selected = new(2026, 7, 28, 14, 42, 17, TimeSpan.FromHours(-7));

        Assert.Equal(
            new DateTimeOffset(2026, 7, 28, 0, 0, 0, TimeSpan.FromHours(-7)),
            CommitDateRangePolicy.StartOfDay(selected));
        Assert.Equal(
            new DateTimeOffset(2026, 7, 28, 23, 59, 59, 999, TimeSpan.FromHours(-7)).AddTicks(9999),
            CommitDateRangePolicy.EndOfDay(selected));
    }

    [Fact]
    public void EmptyDateFiltersRemainUnbounded()
    {
        Assert.Null(CommitDateRangePolicy.StartOfDay(null));
        Assert.Null(CommitDateRangePolicy.EndOfDay(null));
    }
}

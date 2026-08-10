using JitHub.WinUI.Views.Controls.Profile;
using Xunit;

namespace JitHub.WinUI.Tests.Views;

public sealed class ProfileContributionGraphNavigationTests
{
    [Fact]
    public void FindLast_ReturnsLatestAvailableDay()
    {
        ProfileContributionCursor cursor = ProfileContributionGraphNavigation.FindLast([4, 7, 2]);

        Assert.Equal(new ProfileContributionCursor(2, 1), cursor);
    }

    [Fact]
    public void MoveWeek_PreservesDayAndClampsShortWeeks()
    {
        int[] weekDayCounts = [4, 0, 2, 7];

        ProfileContributionCursor next = ProfileContributionGraphNavigation.Move(
            new ProfileContributionCursor(0, 3),
            weekDayCounts,
            ProfileContributionNavigationDirection.NextWeek);
        ProfileContributionCursor previous = ProfileContributionGraphNavigation.Move(
            next,
            weekDayCounts,
            ProfileContributionNavigationDirection.PreviousWeek);

        Assert.Equal(new ProfileContributionCursor(2, 1), next);
        Assert.Equal(new ProfileContributionCursor(0, 1), previous);
    }

    [Fact]
    public void MoveDay_StaysWithinCurrentWeek()
    {
        int[] weekDayCounts = [2];

        Assert.Equal(
            new ProfileContributionCursor(0, 0),
            ProfileContributionGraphNavigation.Move(
                new ProfileContributionCursor(0, 0),
                weekDayCounts,
                ProfileContributionNavigationDirection.PreviousDay));
        Assert.Equal(
            new ProfileContributionCursor(0, 1),
            ProfileContributionGraphNavigation.Move(
                new ProfileContributionCursor(0, 0),
                weekDayCounts,
                ProfileContributionNavigationDirection.NextDay));
        Assert.Equal(
            new ProfileContributionCursor(0, 1),
            ProfileContributionGraphNavigation.Move(
                new ProfileContributionCursor(0, 1),
                weekDayCounts,
                ProfileContributionNavigationDirection.NextDay));
    }

    [Fact]
    public void HomeAndEnd_ReachCalendarBoundsAcrossSparseWeeks()
    {
        int[] weekDayCounts = [0, 3, 0, 5, 2, 0];

        ProfileContributionCursor first = ProfileContributionGraphNavigation.Move(
            new ProfileContributionCursor(3, 2),
            weekDayCounts,
            ProfileContributionNavigationDirection.FirstDay);
        ProfileContributionCursor last = ProfileContributionGraphNavigation.Move(
            first,
            weekDayCounts,
            ProfileContributionNavigationDirection.LastDay);

        Assert.Equal(new ProfileContributionCursor(1, 0), first);
        Assert.Equal(new ProfileContributionCursor(4, 1), last);
    }

    [Fact]
    public void EmptyCalendar_ReturnsInvalidCursor()
    {
        ProfileContributionCursor cursor = ProfileContributionGraphNavigation.Move(
            new ProfileContributionCursor(0, 0),
            [0, 0],
            ProfileContributionNavigationDirection.LastDay);

        Assert.Equal(new ProfileContributionCursor(-1, -1), cursor);
    }

    [Theory]
    [InlineData(760)]
    [InlineData(520)]
    [InlineData(360)]
    [InlineData(232)]
    [InlineData(48)]
    public void LayoutMetrics_AlwaysFitAvailableWidth(double availableWidth)
    {
        const int WeekCount = 53;

        ProfileContributionLayoutMetrics layout =
            ProfileContributionGraphNavigation.CalculateLayout(availableWidth, WeekCount);

        Assert.True(layout.CellSize > 0);
        Assert.True(layout.Gap >= 0);
        Assert.True(layout.WidthFor(WeekCount) <= availableWidth + 0.001);
    }
}

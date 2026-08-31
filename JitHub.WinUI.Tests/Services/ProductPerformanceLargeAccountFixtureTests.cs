using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class ProductPerformanceLargeAccountFixtureTests
{
    [Fact]
    public void Fixture_ProducesDeterministicLargePopulationsForEveryDataShape()
    {
        Assert.Equal(ProductPerformanceLargeAccountFixture.RepositoryCount, ProductPerformanceLargeAccountFixture.CreateRepositories().Length);
        Assert.Equal(ProductPerformanceLargeAccountFixture.StarCount, ProductPerformanceLargeAccountFixture.CreateStars().Length);
        Assert.Equal(ProductPerformanceLargeAccountFixture.WorkItemCount, ProductPerformanceLargeAccountFixture.CreateIssues("owner", "repo", false).Length);
        Assert.Equal(ProductPerformanceLargeAccountFixture.WorkItemCount, ProductPerformanceLargeAccountFixture.CreateIssues("owner", "repo", true).Length);
        Assert.Equal(ProductPerformanceLargeAccountFixture.WorkItemCount, ProductPerformanceLargeAccountFixture.CreatePullRequests("owner", "repo").Length);
        Assert.Equal(ProductPerformanceLargeAccountFixture.NotificationCount, ProductPerformanceLargeAccountFixture.CreateNotifications().Length);
        Assert.Equal(ProductPerformanceLargeAccountFixture.ActivityCount, ProductPerformanceLargeAccountFixture.CreateActivity().Length);
        Assert.Equal(ProductPerformanceLargeAccountFixture.PeopleCount, ProductPerformanceLargeAccountFixture.CreatePeople().Length);
        Assert.Equal(ProductPerformanceLargeAccountFixture.CommitCount, ProductPerformanceLargeAccountFixture.CreateCommits().Length);

        Assert.Equal(
            ProductPerformanceLargeAccountFixture.RepositoryCount,
            ProductPerformanceLargeAccountFixture.CreateRepositories().Select(static repository => repository.Id).Distinct().Count());
        Assert.Equal(53, ProductPerformanceLargeAccountFixture.CreateContributionCalendar().Weeks.Count);
    }
}

using JitHub.Models.NavArgs;
using Xunit;

namespace JitHub.WinUI.Tests.ViewModels;

public sealed class RepositoryBranchNavigationPolicyTests
{
    [Fact]
    public void DefaultBranchIntent_FollowsFreshMetadataAndUsesStableRouteIdentity()
    {
        string target = RepositoryBranchNavigationPolicy.ResolveTargetBranch(
            followsDefaultBranch: true,
            requestedBranch: "3.29",
            currentDefaultBranch: "master");

        Assert.Equal("master", target);
        Assert.Null(RepositoryBranchNavigationPolicy.ResolveRouteIdentityBranch(
            followsDefaultBranch: true,
            requestedBranch: "3.29",
            repositoryDefaultBranch: "3.29"));
        Assert.True(RepositoryBranchNavigationPolicy.ShouldRealign(
            followsDefaultBranch: true,
            currentBranch: "3.29",
            resolvedBranch: "master"));
        Assert.Equal("master", RepositoryBranchNavigationPolicy.ResolveAlignmentBranch(
            followsDefaultBranch: true,
            selectedBranch: "3.29",
            refreshedDefaultBranch: "master"));
    }

    [Fact]
    public void ExplicitBranchIntent_RemainsPinnedAcrossMetadataRefresh()
    {
        string target = RepositoryBranchNavigationPolicy.ResolveTargetBranch(
            followsDefaultBranch: false,
            requestedBranch: "3.29",
            currentDefaultBranch: "master");

        Assert.Equal("3.29", target);
        Assert.Equal("3.29", RepositoryBranchNavigationPolicy.ResolveRouteIdentityBranch(
            followsDefaultBranch: false,
            requestedBranch: "3.29",
            repositoryDefaultBranch: "master"));
        Assert.False(RepositoryBranchNavigationPolicy.ShouldRealign(
            followsDefaultBranch: false,
            currentBranch: "3.29",
            resolvedBranch: "master"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void DefaultBranchIntent_DoesNotNavigateToAnUnknownRef(string? resolvedBranch)
    {
        Assert.False(RepositoryBranchNavigationPolicy.ShouldRealign(
            followsDefaultBranch: true,
            currentBranch: "main",
            resolvedBranch));
    }
}

using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class PullRequestProgressiveSelectionPolicyTests
{
    [Theory]
    [InlineData(42)]
    [InlineData(0)]
    public void UnchangedSelectionGeneration_HonorsTheNavigationPreference(int requestedPreferredNumber)
    {
        int result = PullRequestProgressiveSelectionPolicy.ResolvePreferredNumber(
            loadSelectionGeneration: 7,
            currentSelectionGeneration: 7,
            requestedPreferredNumber,
            currentSelectedNumber: 99);

        Assert.Equal(requestedPreferredNumber, result);
    }

    [Fact]
    public void UserSelectionAfterLoadStart_OwnsLaterProgressPages()
    {
        int result = PullRequestProgressiveSelectionPolicy.ResolvePreferredNumber(
            loadSelectionGeneration: 7,
            currentSelectionGeneration: 8,
            requestedPreferredNumber: 42,
            currentSelectedNumber: 99);

        Assert.Equal(99, result);
    }

    [Fact]
    public void UserClearedSelectionAfterLoadStart_DoesNotFallBackToTheFirstRow()
    {
        int result = PullRequestProgressiveSelectionPolicy.ResolvePreferredNumber(
            loadSelectionGeneration: 7,
            currentSelectionGeneration: 8,
            requestedPreferredNumber: 42,
            currentSelectedNumber: null);

        Assert.Equal(-1, result);
    }
}

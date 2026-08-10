using JitHub.WinUI.ViewModels.Common;
using Xunit;

namespace JitHub.WinUI.Tests.ViewModels;

public sealed class RepositoryMutationOwnershipTests
{
    [Fact]
    public void AccountSwitchSuppressesOldMutationUiPublication()
    {
        RepositoryMutationOwnership ownership = new("account-a", "session-a", 7, 3, 5);

        Assert.False(ownership.CanPublish("account-b", "session-a", 7, 3, 5));
        Assert.True(ownership.CanPublish("account-a", "session-a", 7, 3, 5));
    }

    [Fact]
    public void SameAccountReauthenticationSuppressesOldMutationUiPublication()
    {
        string originalSession = RepositoryMutationOwnership.CreateSessionFingerprint("token-a");
        string refreshedSession = RepositoryMutationOwnership.CreateSessionFingerprint("token-b");
        RepositoryMutationOwnership ownership = new("account-a", originalSession, 7, 3, 5);

        Assert.False(ownership.CanPublish("account-a", refreshedSession, 7, 3, 5));
        Assert.True(ownership.CanPublish("account-a", originalSession, 7, 3, 5));
        Assert.DoesNotContain("token-a", originalSession, System.StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(8, 3, 5)]
    [InlineData(7, 4, 5)]
    [InlineData(7, 3, 6)]
    public void NewRepositoryGenerationOrMutationSuppressesOldPublication(
        long repositoryId,
        long generation,
        long mutationVersion)
    {
        RepositoryMutationOwnership ownership = new("account-a", "session-a", 7, 3, 5);

        Assert.False(ownership.CanPublish("account-a", "session-a", repositoryId, generation, mutationVersion));
    }
}

using System;
using System.Net;
using System.Threading.Tasks;
using JitHub.Models.GitHub;
using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class IssueCapabilityDenialStateTests
{
    [Fact]
    public void ForbiddenMutationDegradesOnlyTheRejectedCapability()
    {
        IssueCapabilityDenialState state = new();

        Assert.True(state.RecordFailure(42, IssueDeniedCapability.Metadata, HttpStatusCode.Forbidden));

        Assert.True(state.IsDenied(IssueDeniedCapability.Metadata));
        Assert.False(state.IsDenied(IssueDeniedCapability.Edit));
        Assert.False(state.IsDenied(IssueDeniedCapability.State));
        Assert.False(state.IsDenied(IssueDeniedCapability.Comment));
        Assert.False(state.IsDenied(IssueDeniedCapability.Reaction));
    }

    [Fact]
    public void NonForbiddenFailureDoesNotDegradeCapabilities()
    {
        IssueCapabilityDenialState state = new();

        Assert.False(state.RecordFailure(42, IssueDeniedCapability.Edit, HttpStatusCode.ServiceUnavailable));

        Assert.Equal(IssueDeniedCapability.None, state.DeniedCapabilities);
    }

    [Fact]
    public void SuccessfulAuthoritativeRefreshRecoversSameIssueCapabilities()
    {
        IssueCapabilityDenialState state = new();
        state.TrackTarget("owner/repository", 42);
        state.RecordFailure(42, IssueDeniedCapability.Edit | IssueDeniedCapability.Reaction, HttpStatusCode.Forbidden);

        Assert.True(state.ConfirmAuthoritativeRefresh("owner/repository", 42));

        Assert.Equal(42, state.IssueNumber);
        Assert.Equal(IssueDeniedCapability.None, state.DeniedCapabilities);
        Assert.False(state.IsDenied(IssueDeniedCapability.Edit));
        Assert.False(state.IsDenied(IssueDeniedCapability.Reaction));
    }

    [Fact]
    public void SelectingAnotherIssueStartsWithFreshCapabilities()
    {
        IssueCapabilityDenialState state = new();
        state.RecordFailure(42, IssueDeniedCapability.Comment, HttpStatusCode.Forbidden);

        state.TrackIssue(43);

        Assert.Equal(43, state.IssueNumber);
        Assert.Equal(IssueDeniedCapability.None, state.DeniedCapabilities);
    }

    [Fact]
    public void SelectingAnotherIssuePreservesRepositoryScopedCreateDenial()
    {
        IssueCapabilityDenialState state = new();
        state.TrackTarget("owner/repository", 42);
        Assert.True(state.RecordRepositoryFailureForCurrent(
            "owner/repository",
            IssueDeniedCapability.Create,
            HttpStatusCode.Forbidden));

        state.TrackIssue(43);

        Assert.Equal(43, state.IssueNumber);
        Assert.True(state.IsDenied(IssueDeniedCapability.Create));
    }

    [Fact]
    public void DelayedFailureForPreviouslySelectedIssueDoesNotAffectCurrentIssue()
    {
        IssueCapabilityDenialState state = new();
        state.TrackIssue(42);
        state.TrackIssue(43);

        Assert.False(state.RecordFailureForCurrent(
            42,
            IssueDeniedCapability.Edit,
            HttpStatusCode.Forbidden));

        Assert.Equal(43, state.IssueNumber);
        Assert.Equal(IssueDeniedCapability.None, state.DeniedCapabilities);
    }

    [Fact]
    public void DelayedCreateFailureForPreviousRepositoryDoesNotAffectCurrentRepository()
    {
        IssueCapabilityDenialState state = new();
        state.TrackTarget("owner/first", 42);
        state.TrackTarget("owner/second", 7);

        Assert.False(state.RecordRepositoryFailureForCurrent(
            "owner/first",
            IssueDeniedCapability.Create,
            HttpStatusCode.Forbidden));

        Assert.Equal("OWNER/SECOND", state.RepositoryIdentity);
        Assert.Equal(IssueDeniedCapability.None, state.DeniedCapabilities);
    }

    [Fact]
    public void IssueRefreshWithoutMatchingAuthoritativeRepositoryDoesNotClearDenials()
    {
        IssueCapabilityDenialState state = new();
        state.TrackTarget("owner/repository", 42);
        state.RecordFailure(42, IssueDeniedCapability.Edit, HttpStatusCode.Forbidden);
        state.RecordRepositoryFailureForCurrent(
            "owner/repository",
            IssueDeniedCapability.Create,
            HttpStatusCode.Forbidden);

        Assert.False(state.ConfirmAuthoritativeRefresh("owner/other", 42));
        Assert.False(state.ConfirmAuthoritativeRefresh("owner/repository", 43));
        Assert.True(state.IsDenied(IssueDeniedCapability.Edit));
        Assert.True(state.IsDenied(IssueDeniedCapability.Create));
    }

    [Fact]
    public void IssueDenialStateDoesNotExposeIssueOnlyRecovery()
    {
        Assert.Null(typeof(IssueCapabilityDenialState).GetMethod("ConfirmSuccessfulRefresh"));
    }

    [Fact]
    public async Task DelayedCreateForbiddenAfterRepositorySwitch_IsSuppressed()
    {
        IssueCapabilityDenialState state = new();
        state.TrackTarget("owner/first", 42);
        IssueCapabilityTarget createTarget = state.CaptureTarget();
        IssueCapabilityRecoveryCoordinator coordinator = new(state);
        TaskCompletionSource releaseFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<bool> CompleteCreateAsync()
        {
            await releaseFailure.Task;
            return coordinator.RecordRepositoryFailure(
                createTarget,
                IssueDeniedCapability.Create,
                HttpStatusCode.Forbidden);
        }

        Task<bool> delayedCreate = CompleteCreateAsync();
        state.TrackTarget("owner/second", 42);
        releaseFailure.SetResult();

        Assert.False(await delayedCreate);
        Assert.Equal("OWNER/SECOND", state.RepositoryIdentity);
        Assert.False(state.IsDenied(IssueDeniedCapability.Create));
    }

    [Fact]
    public async Task OrdinaryIssueRefreshWithoutAuthoritativeRepository_DoesNotClearDenial()
    {
        IssueCapabilityDenialState state = new();
        state.TrackTarget("owner/repository", 42);
        state.RecordFailure(42, IssueDeniedCapability.Edit, HttpStatusCode.Forbidden);
        IssueCapabilityRecoveryCoordinator coordinator = new(state);

        IssueCapabilityRecoveryResult result = await coordinator.RecoverAfterIssueRefreshAsync(
            state.CaptureTarget(),
            refreshedIssueNumber: 42,
            (policy, _) =>
            {
                Assert.Equal(QueryFetchPolicy.NetworkOnly, policy);
                return Task.FromResult<GitHubRepository?>(null);
            });

        Assert.False(result.WasApplied);
        Assert.True(state.IsDenied(IssueDeniedCapability.Edit));
    }

    [Fact]
    public async Task AuthoritativeRecovery_UsesNetworkOnlyAndClearsCurrentTargetDenials()
    {
        IssueCapabilityDenialState state = new();
        state.TrackTarget("owner/repository", 42);
        state.RecordFailure(42, IssueDeniedCapability.Edit, HttpStatusCode.Forbidden);
        state.RecordRepositoryFailureForCurrent(
            state.CaptureTarget(),
            IssueDeniedCapability.Create,
            HttpStatusCode.Forbidden);
        IssueCapabilityRecoveryCoordinator coordinator = new(state);
        int fetchCount = 0;

        IssueCapabilityRecoveryResult result = await coordinator.RecoverAfterIssueRefreshAsync(
            state.CaptureTarget(),
            refreshedIssueNumber: 42,
            (policy, _) =>
            {
                fetchCount++;
                Assert.Equal(QueryFetchPolicy.NetworkOnly, policy);
                return Task.FromResult<GitHubRepository?>(new GitHubRepository());
            });

        Assert.Equal(1, fetchCount);
        Assert.True(result.WasApplied);
        Assert.True(result.DenialsCleared);
        Assert.NotNull(result.Repository);
        Assert.Equal(IssueDeniedCapability.None, state.DeniedCapabilities);
    }

    [Fact]
    public async Task OutOfOrderRepositoryResponseAfterRouteChange_IsSuppressed()
    {
        IssueCapabilityDenialState state = new();
        state.TrackTarget("owner/first", 42);
        state.RecordRepositoryFailureForCurrent(
            state.CaptureTarget(),
            IssueDeniedCapability.Create,
            HttpStatusCode.Forbidden);
        IssueCapabilityTarget firstTarget = state.CaptureTarget();
        IssueCapabilityRecoveryCoordinator coordinator = new(state);
        TaskCompletionSource<GitHubRepository?> repositoryResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<IssueCapabilityRecoveryResult> recovery = coordinator.RecoverRepositoryAsync(
            firstTarget,
            (policy, _) =>
            {
                Assert.Equal(QueryFetchPolicy.NetworkOnly, policy);
                return repositoryResponse.Task;
            });

        state.TrackTarget("owner/second", 7);
        repositoryResponse.SetResult(new GitHubRepository());
        IssueCapabilityRecoveryResult result = await recovery;

        Assert.False(result.WasApplied);
        Assert.False(result.DenialsCleared);
        Assert.Null(result.Repository);
        Assert.Equal("OWNER/SECOND", state.RepositoryIdentity);
        Assert.Equal(IssueDeniedCapability.None, state.DeniedCapabilities);
    }

    [Fact]
    public async Task OutOfOrderIssueResponseForAnotherNumber_DoesNotFetchOrClear()
    {
        IssueCapabilityDenialState state = new();
        state.TrackTarget("owner/repository", 42);
        state.RecordFailure(42, IssueDeniedCapability.Edit, HttpStatusCode.Forbidden);
        IssueCapabilityRecoveryCoordinator coordinator = new(state);
        bool fetched = false;

        IssueCapabilityRecoveryResult result = await coordinator.RecoverAfterIssueRefreshAsync(
            state.CaptureTarget(),
            refreshedIssueNumber: 43,
            (_, _) =>
            {
                fetched = true;
                return Task.FromResult<GitHubRepository?>(new GitHubRepository());
            });

        Assert.False(fetched);
        Assert.False(result.WasApplied);
        Assert.True(state.IsDenied(IssueDeniedCapability.Edit));
    }

    [Fact]
    public async Task DelayedIssueResponseAfterIssueSwitch_DoesNotFetchOrClearRepositoryDenial()
    {
        IssueCapabilityDenialState state = new();
        state.TrackTarget("owner/repository", 42);
        state.RecordRepositoryFailureForCurrent(
            state.CaptureTarget(),
            IssueDeniedCapability.Create,
            HttpStatusCode.Forbidden);
        IssueCapabilityTarget firstIssue = state.CaptureTarget();
        IssueCapabilityRecoveryCoordinator coordinator = new(state);
        TaskCompletionSource<int> issueResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool fetched = false;

        async Task<IssueCapabilityRecoveryResult> FinishIssueRefreshAsync()
        {
            int refreshedIssueNumber = await issueResponse.Task;
            return await coordinator.RecoverAfterIssueRefreshAsync(
                firstIssue,
                refreshedIssueNumber,
                (_, _) =>
                {
                    fetched = true;
                    return Task.FromResult<GitHubRepository?>(new GitHubRepository());
                });
        }

        Task<IssueCapabilityRecoveryResult> recovery = FinishIssueRefreshAsync();
        state.TrackIssue(43);
        issueResponse.SetResult(42);
        IssueCapabilityRecoveryResult result = await recovery;

        Assert.False(fetched);
        Assert.False(result.WasApplied);
        Assert.True(state.IsDenied(IssueDeniedCapability.Create));
        Assert.Equal(43, state.IssueNumber);
    }
}

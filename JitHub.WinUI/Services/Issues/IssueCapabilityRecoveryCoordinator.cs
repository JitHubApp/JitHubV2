using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;

namespace JitHub.Services;

public readonly record struct IssueCapabilityRecoveryResult(
    GitHubRepository? Repository,
    bool WasApplied,
    bool DenialsCleared);

public sealed class IssueCapabilityRecoveryCoordinator
{
    private readonly IssueCapabilityDenialState _state;

    public IssueCapabilityRecoveryCoordinator(IssueCapabilityDenialState state)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
    }

    public bool RecordIssueFailure(
        IssueCapabilityTarget target,
        IssueDeniedCapability capability,
        HttpStatusCode statusCode) =>
        _state.RecordFailureForCurrent(target, capability, statusCode);

    public bool RecordRepositoryFailure(
        IssueCapabilityTarget target,
        IssueDeniedCapability capability,
        HttpStatusCode statusCode) =>
        _state.RecordRepositoryFailureForCurrent(target, capability, statusCode);

    public async Task<IssueCapabilityRecoveryResult> RecoverAfterIssueRefreshAsync(
        IssueCapabilityTarget target,
        int refreshedIssueNumber,
        Func<QueryFetchPolicy, CancellationToken, Task<GitHubRepository?>> fetchRepositoryAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fetchRepositoryAsync);
        if (!_state.HasDenials || !_state.IsCurrent(target) || refreshedIssueNumber != target.IssueNumber)
        {
            return default;
        }

        GitHubRepository? repository = await fetchRepositoryAsync(
            QueryFetchPolicy.NetworkOnly,
            cancellationToken).ConfigureAwait(false);
        if (repository is null || !_state.IsCurrent(target))
        {
            return default;
        }

        bool cleared = _state.ConfirmAuthoritativeRefresh(target, refreshedIssueNumber);
        return new IssueCapabilityRecoveryResult(repository, cleared, cleared);
    }

    public async Task<IssueCapabilityRecoveryResult> RecoverRepositoryAsync(
        IssueCapabilityTarget target,
        Func<QueryFetchPolicy, CancellationToken, Task<GitHubRepository?>> fetchRepositoryAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fetchRepositoryAsync);
        if (!_state.IsCurrent(target))
        {
            return default;
        }

        GitHubRepository? repository = await fetchRepositoryAsync(
            QueryFetchPolicy.NetworkOnly,
            cancellationToken).ConfigureAwait(false);
        if (repository is null || !_state.IsCurrent(target))
        {
            return default;
        }

        bool cleared = _state.ConfirmAuthoritativeRepositoryRefresh(target);
        return new IssueCapabilityRecoveryResult(repository, true, cleared);
    }
}

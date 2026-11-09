using System;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;

namespace JitHub.Services;

public sealed class NotificationOpenWorkflow
{
    private readonly NotificationInboxState _inboxState;
    private readonly IGitHubNotificationQueryService _queryService;

    public NotificationOpenWorkflow(
        NotificationInboxState inboxState,
        IGitHubNotificationQueryService queryService)
    {
        _inboxState = inboxState;
        _queryService = queryService;
    }

    public async Task ExecuteAsync(
        string? accessToken,
        string accountId,
        GitHubNotificationThread notification,
        Action navigate,
        Action projectSharedReadState,
        Action reportRemoteFailure)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ArgumentNullException.ThrowIfNull(navigate);
        ArgumentNullException.ThrowIfNull(projectSharedReadState);
        ArgumentNullException.ThrowIfNull(reportRemoteFailure);

        string threadId = notification.Id?.Trim() ?? string.Empty;
        bool isUnread = notification.Unread;
        if (!string.IsNullOrWhiteSpace(accessToken) &&
            !string.IsNullOrWhiteSpace(threadId) &&
            _inboxState.TryGetThreadUnreadState(accountId, threadId, out bool sharedUnread))
        {
            isUnread = sharedUnread;
        }

        if (string.IsNullOrWhiteSpace(accessToken) ||
            string.IsNullOrWhiteSpace(threadId) ||
            !isUnread)
        {
            navigate();
            return;
        }

        NotificationMutationLease lease = _inboxState.BeginReadStateMutation(
            accountId,
            threadId,
            wasUnread: true,
            isUnread: false);
        projectSharedReadState();

        Task remoteMutation;
        try
        {
            remoteMutation = _queryService.MarkThreadReadAsync(
                accessToken,
                accountId,
                threadId,
                CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            _inboxState.CompleteMutation(lease);
            navigate();
            return;
        }
        catch (Exception)
        {
            _inboxState.RollbackMutation(lease);
            projectSharedReadState();
            reportRemoteFailure();
            navigate();
            return;
        }

        navigate();
        try
        {
            await remoteMutation;
            _inboxState.CompleteMutation(lease);
        }
        catch (OperationCanceledException)
        {
            // Cancellation cannot establish that GitHub rejected an idempotent write.
            _inboxState.CompleteMutation(lease);
        }
        catch (Exception)
        {
            _inboxState.RollbackMutation(lease);
            projectSharedReadState();
            reportRemoteFailure();
        }
    }
}

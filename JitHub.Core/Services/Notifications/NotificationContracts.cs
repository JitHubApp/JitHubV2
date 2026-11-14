using System.Threading;
using System.Threading.Tasks;
using JitHub.Core.Models.GitHub.Notifications;
using JitHub.Core.Services.GitHub.Caching;

namespace JitHub.Core.Services.Notifications;

public enum NotificationListFilter
{
    Unread,
    All,
    Participating
}

public interface IGitHubNotificationQueryService
{
    Task<CachedResult<GitHubNotificationThread[]>> GetPageAsync(
        string accessToken,
        string userId,
        NotificationListFilter filter,
        int page,
        int pageSize,
        QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst,
        CancellationToken cancellationToken = default);

    Task<CachedResult<GitHubNotificationSubscription>> GetSubscriptionAsync(
        string accessToken,
        string userId,
        string threadId,
        QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst,
        CancellationToken cancellationToken = default);

    Task MarkAllReadAsync(string accessToken, string userId, CancellationToken cancellationToken = default);

    Task MarkThreadReadAsync(string accessToken, string userId, string threadId, CancellationToken cancellationToken = default);

    Task<GitHubNotificationSubscription> SubscribeThreadAsync(
        string accessToken,
        string userId,
        string threadId,
        CancellationToken cancellationToken = default);

    Task UnsubscribeThreadAsync(
        string accessToken,
        string userId,
        string threadId,
        CancellationToken cancellationToken = default);

    Task<GitHubNotificationSubscription> MuteThreadAsync(
        string accessToken,
        string userId,
        string threadId,
        CancellationToken cancellationToken = default);

    Task<GitHubNotificationSubscription> UnmuteThreadAsync(
        string accessToken,
        string userId,
        string threadId,
        CancellationToken cancellationToken = default);
}

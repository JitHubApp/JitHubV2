using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using JitHub.Models.GitHub;

namespace JitHub.Services;

public enum NotificationCountSource
{
    HomePreview,
    AccountWideWorkspace
}

public sealed class NotificationMutationLease
{
    internal NotificationMutationLease(string accountId, long id)
    {
        AccountId = accountId;
        Id = id;
    }

    internal string AccountId { get; }

    internal long Id { get; }
}

public sealed class NotificationInboxState : ObservableObject
{
    private sealed record ActiveMutation(
        int UnreadDelta,
        bool RestoresFullCount,
        int PreviousUnreadCount,
        bool PreviousIsPartial,
        bool PreviousHasCount,
        NotificationCountSource PreviousCountSource,
        string? ThreadId = null,
        bool HadPreviousThreadState = false,
        bool PreviousThreadUnread = false,
        IReadOnlyDictionary<string, bool>? PreviousThreadStates = null);

    private readonly Dictionary<long, ActiveMutation> _activeMutations = [];
    private readonly Dictionary<string, bool> _threadUnreadStates = new(StringComparer.Ordinal);
    private string _accountId = string.Empty;
    private int _unreadCount;
    private bool _isPartial;
    private DateTimeOffset? _updatedAt;
    private long _mutationGeneration;
    private long _nextMutationId;
    private int _readStateVersion;
    private bool _hasCount;
    private NotificationCountSource _countSource;

    public int UnreadCount => _unreadCount;

    public bool IsPartial => _isPartial;

    public bool HasActiveMutations => _activeMutations.Count > 0;

    public int ReadStateVersion => _readStateVersion;

    public string BadgeText => _unreadCount <= 0
        ? string.Empty
        : _unreadCount >= 100
            ? "99+"
            : _isPartial
                ? $"{_unreadCount}+"
                : _unreadCount.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public long CaptureMutationGeneration(string accountId)
    {
        ResetForAccount(accountId);
        return _mutationGeneration;
    }

    public bool IsCurrentMutationGeneration(string accountId, long generation)
    {
        ResetForAccount(accountId);
        return _activeMutations.Count == 0 && _mutationGeneration == generation;
    }

    public bool ApplySnapshot(
        string accountId,
        IEnumerable<GitHubNotificationThread> threads,
        bool isPartial,
        DateTimeOffset? fetchedAt,
        NotificationCountSource source,
        long? expectedMutationGeneration = null)
    {
        ArgumentNullException.ThrowIfNull(threads);
        ResetForAccount(accountId);

        if (_activeMutations.Count > 0 ||
            (expectedMutationGeneration is not null && expectedMutationGeneration.Value != _mutationGeneration))
        {
            return false;
        }

        GitHubNotificationThread[] snapshot = threads as GitHubNotificationThread[] ?? threads.ToArray();
        int unreadCount = snapshot.Count(static thread => thread.Unread);
        bool partial = isPartial || (source == NotificationCountSource.HomePreview && unreadCount >= 10);
        if (HasHigherAuthorityThan(source, partial))
        {
            return false;
        }

        DateTimeOffset candidateTimestamp = fetchedAt ?? DateTimeOffset.MinValue;
        if (HasSameAuthorityAs(source, partial) &&
            _updatedAt is not null &&
            candidateTimestamp < _updatedAt.Value)
        {
            return false;
        }

        ApplyThreadSnapshot(snapshot);
        SetCount(unreadCount, partial, fetchedAt, source);
        return true;
    }

    public NotificationMutationLease BeginReadStateMutation(
        string accountId,
        string threadId,
        bool wasUnread,
        bool isUnread)
    {
        ResetForAccount(accountId);
        string normalizedThreadId = NormalizeThreadId(threadId);
        bool hadPreviousState = _threadUnreadStates.TryGetValue(normalizedThreadId, out bool previousThreadUnread);
        int delta = wasUnread == isUnread ? 0 : isUnread ? 1 : -1;
        NotificationMutationLease lease = BeginMutation(
            accountId,
            new ActiveMutation(
                delta,
                false,
                _unreadCount,
                _isPartial,
                _hasCount,
                _countSource,
                normalizedThreadId,
                hadPreviousState,
                hadPreviousState ? previousThreadUnread : wasUnread));
        SetThreadUnreadState(normalizedThreadId, isUnread);
        if (delta != 0)
        {
            SetCount(Math.Max(0, _unreadCount + delta), _isPartial, DateTimeOffset.UtcNow);
        }

        return lease;
    }

    public NotificationMutationLease BeginMarkAllReadMutation(string accountId)
    {
        ResetForAccount(accountId);
        Dictionary<string, bool> previousThreadStates = new(_threadUnreadStates, StringComparer.Ordinal);
        NotificationMutationLease lease = BeginMutation(
            accountId,
            new ActiveMutation(
                0,
                true,
                _unreadCount,
                _isPartial,
                _hasCount,
                _countSource,
                PreviousThreadStates: previousThreadStates));
        SetAllThreadsRead();
        SetCount(0, false, DateTimeOffset.UtcNow, NotificationCountSource.AccountWideWorkspace);
        return lease;
    }

    public NotificationMutationLease BeginSubscriptionMutation(string accountId)
    {
        ResetForAccount(accountId);
        return BeginMutation(
            accountId,
            new ActiveMutation(0, false, _unreadCount, _isPartial, _hasCount, _countSource));
    }

    public bool CompleteMutation(NotificationMutationLease lease) =>
        FinishMutation(lease, rollback: false);

    public bool RollbackMutation(NotificationMutationLease lease) =>
        FinishMutation(lease, rollback: true);

    public void SetReadState(string accountId, bool wasUnread, bool isUnread)
    {
        NotificationMutationLease lease = BeginReadStateMutation(accountId, string.Empty, wasUnread, isUnread);
        CompleteMutation(lease);
    }

    public void SetReadState(string accountId, string threadId, bool wasUnread, bool isUnread)
    {
        NotificationMutationLease lease = BeginReadStateMutation(accountId, threadId, wasUnread, isUnread);
        CompleteMutation(lease);
    }

    public bool TryGetThreadUnreadState(string accountId, string threadId, out bool isUnread)
    {
        ResetForAccount(accountId);
        return _threadUnreadStates.TryGetValue(NormalizeThreadId(threadId), out isUnread);
    }

    public void MarkAllRead(string accountId)
    {
        NotificationMutationLease lease = BeginMarkAllReadMutation(accountId);
        CompleteMutation(lease);
    }

    private NotificationMutationLease BeginMutation(string accountId, ActiveMutation mutation)
    {
        string normalizedAccountId = NormalizeAccountId(accountId);
        long id = ++_nextMutationId;
        _activeMutations.Add(id, mutation);
        _mutationGeneration++;
        OnPropertyChanged(nameof(HasActiveMutations));
        return new NotificationMutationLease(normalizedAccountId, id);
    }

    private bool FinishMutation(NotificationMutationLease lease, bool rollback)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (!string.Equals(_accountId, lease.AccountId, StringComparison.Ordinal) ||
            !_activeMutations.Remove(lease.Id, out ActiveMutation? mutation))
        {
            return false;
        }

        _mutationGeneration++;
        if (rollback)
        {
            if (mutation.RestoresFullCount)
            {
                SetCount(
                    mutation.PreviousUnreadCount,
                    mutation.PreviousIsPartial,
                    DateTimeOffset.UtcNow,
                    mutation.PreviousCountSource);
                _hasCount = mutation.PreviousHasCount;
            }
            else if (mutation.UnreadDelta != 0)
            {
                SetCount(Math.Max(0, _unreadCount - mutation.UnreadDelta), _isPartial, DateTimeOffset.UtcNow);
            }

            RestoreThreadStates(mutation);
        }

        OnPropertyChanged(nameof(HasActiveMutations));
        return true;
    }

    private void ResetForAccount(string accountId)
    {
        string normalized = NormalizeAccountId(accountId);
        if (string.Equals(_accountId, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _accountId = normalized;
        _unreadCount = 0;
        _isPartial = false;
        _updatedAt = null;
        _mutationGeneration = 0;
        _hasCount = false;
        _countSource = default;
        _activeMutations.Clear();
        _threadUnreadStates.Clear();
        OnPropertyChanged(nameof(UnreadCount));
        OnPropertyChanged(nameof(IsPartial));
        OnPropertyChanged(nameof(HasActiveMutations));
        OnPropertyChanged(nameof(BadgeText));
        AdvanceReadStateVersion();
    }

    private void SetCount(
        int unreadCount,
        bool isPartial,
        DateTimeOffset? updatedAt,
        NotificationCountSource? source = null)
    {
        bool countChanged = _unreadCount != unreadCount;
        bool partialChanged = _isPartial != isPartial;
        _unreadCount = unreadCount;
        _isPartial = isPartial;
        _hasCount = true;
        if (source is not null)
        {
            _countSource = source.Value;
        }
        _updatedAt = updatedAt ?? _updatedAt;

        if (countChanged)
        {
            OnPropertyChanged(nameof(UnreadCount));
        }

        if (partialChanged)
        {
            OnPropertyChanged(nameof(IsPartial));
        }

        if (countChanged || partialChanged)
        {
            OnPropertyChanged(nameof(BadgeText));
        }
    }

    private bool HasHigherAuthorityThan(NotificationCountSource source, bool isPartial)
    {
        if (!_hasCount)
        {
            return false;
        }

        return CountAuthority(_countSource, _isPartial) > CountAuthority(source, isPartial);
    }

    private bool HasSameAuthorityAs(NotificationCountSource source, bool isPartial) =>
        _hasCount && CountAuthority(_countSource, _isPartial) == CountAuthority(source, isPartial);

    private static int CountAuthority(NotificationCountSource source, bool isPartial) =>
        source switch
        {
            NotificationCountSource.AccountWideWorkspace when !isPartial => 3,
            NotificationCountSource.AccountWideWorkspace => 2,
            NotificationCountSource.HomePreview when !isPartial => 2,
            _ => 1
        };

    private void ApplyThreadSnapshot(IEnumerable<GitHubNotificationThread> threads)
    {
        bool changed = false;
        foreach (GitHubNotificationThread thread in threads)
        {
            if (string.IsNullOrWhiteSpace(thread.Id))
            {
                continue;
            }

            string threadId = NormalizeThreadId(thread.Id);
            if (!_threadUnreadStates.TryGetValue(threadId, out bool current) || current != thread.Unread)
            {
                _threadUnreadStates[threadId] = thread.Unread;
                changed = true;
            }
        }

        if (changed)
        {
            AdvanceReadStateVersion();
        }
    }

    private void SetThreadUnreadState(string threadId, bool isUnread)
    {
        if (string.IsNullOrEmpty(threadId))
        {
            return;
        }

        if (_threadUnreadStates.TryGetValue(threadId, out bool current) && current == isUnread)
        {
            return;
        }

        _threadUnreadStates[threadId] = isUnread;
        AdvanceReadStateVersion();
    }

    private void SetAllThreadsRead()
    {
        bool changed = false;
        foreach (string threadId in _threadUnreadStates.Keys.ToArray())
        {
            if (_threadUnreadStates[threadId])
            {
                _threadUnreadStates[threadId] = false;
                changed = true;
            }
        }

        if (changed)
        {
            AdvanceReadStateVersion();
        }
    }

    private void RestoreThreadStates(ActiveMutation mutation)
    {
        if (mutation.PreviousThreadStates is not null)
        {
            _threadUnreadStates.Clear();
            foreach ((string threadId, bool unread) in mutation.PreviousThreadStates)
            {
                _threadUnreadStates[threadId] = unread;
            }

            AdvanceReadStateVersion();
            return;
        }

        if (mutation.ThreadId is null)
        {
            return;
        }

        if (mutation.HadPreviousThreadState)
        {
            _threadUnreadStates[mutation.ThreadId] = mutation.PreviousThreadUnread;
        }
        else
        {
            _threadUnreadStates.Remove(mutation.ThreadId);
        }

        AdvanceReadStateVersion();
    }

    private void AdvanceReadStateVersion()
    {
        _readStateVersion++;
        OnPropertyChanged(nameof(ReadStateVersion));
    }

    private static string NormalizeAccountId(string accountId) =>
        string.IsNullOrWhiteSpace(accountId) ? "current" : accountId.Trim();

    private static string NormalizeThreadId(string threadId) =>
        string.IsNullOrWhiteSpace(threadId) ? string.Empty : threadId.Trim();
}

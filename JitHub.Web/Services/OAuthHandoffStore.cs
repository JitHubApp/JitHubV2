using System.Collections.Concurrent;
using JitHub.Security;

namespace JitHub.Web.Services;

internal sealed class OAuthHandoffStore
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);
    private readonly IOAuthHandoffBackend _backend;
    private readonly TimeProvider _timeProvider;

    public OAuthHandoffStore(TimeProvider timeProvider, IOAuthHandoffBackend backend)
    {
        _timeProvider = timeProvider;
        _backend = backend;
    }

    internal OAuthHandoffStore(TimeProvider timeProvider)
        : this(timeProvider, new InMemoryOAuthHandoffBackend(timeProvider))
    {
    }

    public async Task<string> CreateAsync(
        string token,
        string state,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (!OAuthHandoffProtocol.TryGetChallenge(state, out string challenge))
        {
            throw new InvalidOperationException("Invalid OAuth handoff state.");
        }

        DateTimeOffset expiresAt = _timeProvider.GetUtcNow().Add(Lifetime);
        for (int attempt = 0; attempt < 4; attempt++)
        {
            string id = OAuthHandoffProtocol.CreateBase64UrlSecret(32);
            if (await _backend.TryCreateAsync(
                    id,
                    new OAuthHandoffEntry(token, state, challenge, expiresAt),
                    Lifetime,
                    cancellationToken))
            {
                return id;
            }
        }

        throw new InvalidOperationException("Could not allocate an OAuth handoff.");
    }

    public async Task<string?> RedeemAsync(
        string? handoff,
        string? state,
        string? verifier,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(handoff) ||
            string.IsNullOrWhiteSpace(state) ||
            string.IsNullOrWhiteSpace(verifier))
        {
            return null;
        }

        OAuthHandoffEntry? entry = await _backend.ConsumeAsync(handoff, cancellationToken);
        if (entry is null)
        {
            return null;
        }
        if (entry.ExpiresAt <= _timeProvider.GetUtcNow() ||
            !string.Equals(entry.State, state, StringComparison.Ordinal) ||
            !OAuthHandoffProtocol.Verify(verifier, entry.Challenge))
        {
            return null;
        }

        return entry.Token;
    }
}

internal sealed record OAuthHandoffEntry(
    string Token,
    string State,
    string Challenge,
    DateTimeOffset ExpiresAt);

internal interface IOAuthHandoffBackend
{
    Task<bool> TryCreateAsync(
        string id,
        OAuthHandoffEntry entry,
        TimeSpan lifetime,
        CancellationToken cancellationToken);

    Task<OAuthHandoffEntry?> ConsumeAsync(string id, CancellationToken cancellationToken);
}

internal sealed class InMemoryOAuthHandoffBackend(TimeProvider timeProvider) : IOAuthHandoffBackend
{
    private const int MaximumPendingHandoffs = 10_000;
    private readonly ConcurrentDictionary<string, OAuthHandoffEntry> _entries = new(StringComparer.Ordinal);

    private void RemoveExpiredEntries()
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        foreach ((string id, OAuthHandoffEntry entry) in _entries)
        {
            if (entry.ExpiresAt <= now)
            {
                _entries.TryRemove(id, out _);
            }
        }
    }

    public Task<bool> TryCreateAsync(
        string id,
        OAuthHandoffEntry entry,
        TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RemoveExpiredEntries();
        if (_entries.Count >= MaximumPendingHandoffs)
        {
            throw new InvalidOperationException("Too many pending OAuth handoffs.");
        }

        return Task.FromResult(_entries.TryAdd(id, entry));
    }

    public Task<OAuthHandoffEntry?> ConsumeAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _entries.TryRemove(id, out OAuthHandoffEntry? entry);
        return Task.FromResult(entry);
    }
}

internal sealed record OAuthHandoffCreateRequest(string? TempCode, string? RedirectUri, string? State);

internal sealed record OAuthHandoffCreatedResponse(string Handoff);

internal sealed record OAuthHandoffRedeemRequest(string? Handoff, string? State, string? Verifier);

internal sealed record OAuthHandoffRedeemedResponse(string Token);

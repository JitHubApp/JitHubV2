using System;
using System.Globalization;
using System.Threading;
using JitHub.Core.Services.Accounts;

namespace JitHub.Core.Services.Images;

/// <summary>
/// Partitions image work by the signed-in account: it names the account whose
/// cache partition a request uses, and enters that account's work quiescence
/// so sign-out cancels and drains in-flight image work.
/// </summary>
internal sealed class GitHubImageAccountScope
{
    private readonly Func<long> _partitionProvider;
    private readonly IAccountWorkQuiescence? _accountWork;

    internal GitHubImageAccountScope(Func<long>? partitionProvider, IAccountWorkQuiescence? accountWork)
    {
        _partitionProvider = partitionProvider ?? (() => 0);
        _accountWork = accountWork;
    }

    internal long CurrentAccountId() => _partitionProvider();

    internal IAccountWorkLease? Enter(long accountId, CancellationToken cancellationToken) =>
        accountId > 0
            ? _accountWork?.Enter(accountId.ToString(CultureInfo.InvariantCulture), cancellationToken)
            : null;

    internal static string GetCacheKey(long accountId, string sourceUrl) =>
        $"{accountId.ToString(CultureInfo.InvariantCulture)}:{NormalizeCacheIdentity(sourceUrl)}";

    /// <summary>
    /// Normalizes scheme, host, default port and fragment so equivalent URLs
    /// share one cache entry.
    /// </summary>
    internal static string NormalizeCacheIdentity(string sourceUrl)
    {
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out Uri? uri))
        {
            return sourceUrl.Trim();
        }

        UriBuilder builder = new(uri)
        {
            Scheme = uri.Scheme.ToLowerInvariant(),
            Host = uri.IdnHost.ToLowerInvariant(),
            Fragment = string.Empty,
            Port = uri.IsDefaultPort ? -1 : uri.Port
        };
        return builder.Uri.GetComponents(
            UriComponents.SchemeAndServer | UriComponents.PathAndQuery,
            UriFormat.UriEscaped);
    }
}

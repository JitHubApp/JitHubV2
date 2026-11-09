using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Windows.Networking.Connectivity;

namespace JitHub.Services;

public enum AdaptivePrefetchFeature
{
    Issues,
    PullRequests,
    Commits
}

public enum AdaptivePrefetchStage
{
    Schedule,
    Execute
}

public enum AdaptivePrefetchSuppressionReason
{
    None,
    Offline,
    MeteredConnection,
    EnergySaver,
    MemoryPressure,
    RateLimitHeadroom
}

public readonly record struct AdaptivePrefetchDecision(
    bool IsAllowed,
    AdaptivePrefetchSuppressionReason SuppressionReason);

public sealed record AdaptivePrefetchCounter(
    AdaptivePrefetchFeature Feature,
    AdaptivePrefetchStage Stage,
    AdaptivePrefetchSuppressionReason SuppressionReason,
    bool IsAllowed,
    long Count);

public interface IAdaptivePrefetchPolicy
{
    AdaptivePrefetchDecision Evaluate(
        string accountPartition,
        AdaptivePrefetchFeature feature,
        AdaptivePrefetchStage stage);

    void ObserveRateLimit(
        string accountPartition,
        int? remaining,
        DateTimeOffset? resetAt,
        TimeSpan? retryAfter = null,
        string? resource = null);

    IReadOnlyList<AdaptivePrefetchCounter> GetCounters();
}

internal interface IPrefetchEnvironmentState
{
    bool IsNetworkAvailable { get; }

    bool IsMetered { get; }

    bool IsEnergySaverEnabled { get; }

    bool IsMemoryPressureHigh { get; }
}

internal sealed class WindowsPrefetchEnvironmentState : IPrefetchEnvironmentState
{
    public bool IsNetworkAvailable
    {
        get
        {
            try
            {
                ConnectionProfile? profile = NetworkInformation.GetInternetConnectionProfile();
                return profile?.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess;
            }
            catch
            {
                // Transient WinRT probe failures must not permanently disable prefetch.
                return true;
            }
        }
    }

    public bool IsMetered
    {
        get
        {
            try
            {
                ConnectionCost? cost = NetworkInformation.GetInternetConnectionProfile()?.GetConnectionCost();
                return cost is not null &&
                    (cost.NetworkCostType is NetworkCostType.Fixed or NetworkCostType.Variable ||
                     cost.Roaming ||
                     cost.OverDataLimit);
            }
            catch
            {
                return false;
            }
        }
    }

    public bool IsEnergySaverEnabled
    {
        get
        {
            try
            {
                return Windows.System.Power.PowerManager.EnergySaverStatus ==
                    Windows.System.Power.EnergySaverStatus.On;
            }
            catch
            {
                return false;
            }
        }
    }

    public bool IsMemoryPressureHigh
    {
        get
        {
            try
            {
                return Windows.System.MemoryManager.AppMemoryUsageLevel is
                    Windows.System.AppMemoryUsageLevel.High or
                    Windows.System.AppMemoryUsageLevel.OverLimit;
            }
            catch
            {
                return false;
            }
        }
    }
}

public sealed class AdaptivePrefetchPolicy : IAdaptivePrefetchPolicy
{
    internal const int MinimumRateLimitHeadroom = 100;
    private static readonly TimeSpan UnknownResetSuppressionWindow = TimeSpan.FromMinutes(1);

    private readonly IPrefetchEnvironmentState _environment;
    private readonly ITelemetryService _telemetry;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly ConcurrentDictionary<RateLimitBucketKey, PrimaryRateLimitWindow> _primaryRateLimits = [];
    private readonly ConcurrentDictionary<string, DateTimeOffset> _secondaryRateLimits =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<CounterKey, long> _counters = [];

    internal AdaptivePrefetchPolicy(
        IPrefetchEnvironmentState environment,
        ITelemetryService telemetry,
        Func<DateTimeOffset>? utcNow = null)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _telemetry = SafeTelemetryService.Wrap(telemetry);
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public AdaptivePrefetchPolicy(ITelemetryService telemetry)
        : this(new WindowsPrefetchEnvironmentState(), telemetry)
    {
    }

    public AdaptivePrefetchDecision Evaluate(
        string accountPartition,
        AdaptivePrefetchFeature feature,
        AdaptivePrefetchStage stage)
    {
        AdaptivePrefetchSuppressionReason reason = GetSuppressionReason(accountPartition);
        AdaptivePrefetchDecision decision = new(reason == AdaptivePrefetchSuppressionReason.None, reason);
        RecordDecision(feature, stage, decision);
        return decision;
    }

    public void ObserveRateLimit(
        string accountPartition,
        int? remaining,
        DateTimeOffset? resetAt,
        TimeSpan? retryAfter = null,
        string? resource = null)
    {
        string? partition = NormalizePartition(accountPartition);
        if (partition is null || remaining is null && retryAfter is null)
        {
            return;
        }

        DateTimeOffset now = _utcNow();
        if (retryAfter is TimeSpan delay)
        {
            DateTimeOffset blockedUntil = now.Add(delay > TimeSpan.Zero ? delay : TimeSpan.Zero);
            _secondaryRateLimits.AddOrUpdate(
                partition,
                blockedUntil,
                (_, existing) => existing > blockedUntil ? existing : blockedUntil);
            return;
        }

        PrimaryRateLimitWindow? primary = CreatePrimaryWindow(remaining, resetAt, now);
        if (primary is null)
        {
            return;
        }

        RateLimitBucketKey bucket = new(partition, NormalizeResource(resource));
        _primaryRateLimits.AddOrUpdate(
            bucket,
            primary.Value,
            (_, existing) => MergePrimaryWindow(existing, primary.Value, now));
    }

    public IReadOnlyList<AdaptivePrefetchCounter> GetCounters()
    {
        List<AdaptivePrefetchCounter> snapshot = [];
        foreach ((CounterKey key, long count) in _counters)
        {
            snapshot.Add(new AdaptivePrefetchCounter(
                key.Feature,
                key.Stage,
                key.SuppressionReason,
                key.IsAllowed,
                count));
        }

        return snapshot;
    }

    private AdaptivePrefetchSuppressionReason GetSuppressionReason(string accountPartition)
    {
        if (!_environment.IsNetworkAvailable)
        {
            return AdaptivePrefetchSuppressionReason.Offline;
        }

        if (_environment.IsMetered)
        {
            return AdaptivePrefetchSuppressionReason.MeteredConnection;
        }

        if (_environment.IsEnergySaverEnabled)
        {
            return AdaptivePrefetchSuppressionReason.EnergySaver;
        }

        if (_environment.IsMemoryPressureHigh)
        {
            return AdaptivePrefetchSuppressionReason.MemoryPressure;
        }

        string? partition = NormalizePartition(accountPartition);
        if (partition is not null)
        {
            DateTimeOffset now = _utcNow();
            if (_secondaryRateLimits.TryGetValue(partition, out DateTimeOffset secondaryUntil))
            {
                if (secondaryUntil > now)
                {
                    return AdaptivePrefetchSuppressionReason.RateLimitHeadroom;
                }

                _secondaryRateLimits.TryRemove(
                    new KeyValuePair<string, DateTimeOffset>(partition, secondaryUntil));
            }

            foreach ((RateLimitBucketKey bucket, PrimaryRateLimitWindow primary) in _primaryRateLimits)
            {
                if (!string.Equals(bucket.AccountPartition, partition, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (primary.ResetAt <= now)
                {
                    _primaryRateLimits.TryRemove(
                        new KeyValuePair<RateLimitBucketKey, PrimaryRateLimitWindow>(bucket, primary));
                    continue;
                }

                if (primary.Remaining <= MinimumRateLimitHeadroom)
                {
                    return AdaptivePrefetchSuppressionReason.RateLimitHeadroom;
                }
            }
        }

        return AdaptivePrefetchSuppressionReason.None;
    }

    private void RecordDecision(
        AdaptivePrefetchFeature feature,
        AdaptivePrefetchStage stage,
        AdaptivePrefetchDecision decision)
    {
        CounterKey key = new(feature, stage, decision.SuppressionReason, decision.IsAllowed);
        _counters.AddOrUpdate(key, 1, static (_, current) => current + 1);

        try
        {
            _telemetry.TrackMetric(
                "prefetch.policy.decision",
                1,
                new Dictionary<string, string?>
                {
                    ["feature"] = TelemetryTaxonomy.EnumValue(feature),
                    ["phase"] = TelemetryTaxonomy.EnumValue(stage),
                    ["result"] = decision.IsAllowed ? "allowed" : "suppressed",
                    ["source"] = TelemetryTaxonomy.EnumValue(decision.SuppressionReason)
                });
        }
        catch
        {
            // Diagnostics are best-effort and cannot affect request admission.
        }
    }

    private static PrimaryRateLimitWindow? CreatePrimaryWindow(
        int? remaining,
        DateTimeOffset? resetAt,
        DateTimeOffset observedAt)
    {
        if (remaining is null)
        {
            return null;
        }

        if (resetAt is DateTimeOffset explicitReset)
        {
            return new PrimaryRateLimitWindow(remaining.Value, explicitReset, HasExplicitReset: true);
        }

        return new PrimaryRateLimitWindow(
            remaining.Value,
            observedAt.Add(UnknownResetSuppressionWindow),
            HasExplicitReset: false);
    }

    private static PrimaryRateLimitWindow MergePrimaryWindow(
        PrimaryRateLimitWindow existing,
        PrimaryRateLimitWindow incoming,
        DateTimeOffset observedAt)
    {
        if (incoming.ResetAt <= observedAt)
        {
            return existing.ResetAt > observedAt ? existing : incoming;
        }

        if (existing.ResetAt <= observedAt)
        {
            return incoming;
        }

        PrimaryRateLimitWindow currentWindow = existing;
        PrimaryRateLimitWindow incomingWindow = incoming;
        if (currentWindow.HasExplicitReset && !incomingWindow.HasExplicitReset)
        {
            return currentWindow;
        }

        if (!currentWindow.HasExplicitReset && incomingWindow.HasExplicitReset)
        {
            return incomingWindow;
        }

        int resetComparison = incomingWindow.ResetAt.CompareTo(currentWindow.ResetAt);
        if (resetComparison > 0)
        {
            // A later primary reset is a new rate-limit generation. Its remaining
            // count is authoritative and must not inherit exhaustion from the old one.
            return incomingWindow;
        }

        if (resetComparison < 0)
        {
            // This response belongs to an older generation that completed late.
            return currentWindow;
        }

        return currentWindow with
        {
            Remaining = Math.Min(currentWindow.Remaining, incomingWindow.Remaining)
        };
    }

    private static string? NormalizePartition(string accountPartition)
    {
        string normalized = accountPartition?.Trim() ?? string.Empty;
        return normalized.Length == 0 ||
               normalized.Equals("current", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("anonymous", StringComparison.OrdinalIgnoreCase)
            ? null
            : normalized.ToLowerInvariant();
    }

    private static string NormalizeResource(string? resource)
    {
        string normalized = resource?.Trim() ?? string.Empty;
        return normalized.Length == 0 ? "unknown" : normalized.ToLowerInvariant();
    }

    private readonly record struct PrimaryRateLimitWindow(
        int Remaining,
        DateTimeOffset ResetAt,
        bool HasExplicitReset);

    private readonly record struct RateLimitBucketKey(
        string AccountPartition,
        string Resource);

    private readonly record struct CounterKey(
        AdaptivePrefetchFeature Feature,
        AdaptivePrefetchStage Stage,
        AdaptivePrefetchSuppressionReason SuppressionReason,
        bool IsAllowed);
}

internal sealed class UnrestrictedAdaptivePrefetchPolicy : IAdaptivePrefetchPolicy
{
    public static readonly IAdaptivePrefetchPolicy Instance = new UnrestrictedAdaptivePrefetchPolicy();

    private UnrestrictedAdaptivePrefetchPolicy()
    {
    }

    public AdaptivePrefetchDecision Evaluate(
        string accountPartition,
        AdaptivePrefetchFeature feature,
        AdaptivePrefetchStage stage) =>
        new(true, AdaptivePrefetchSuppressionReason.None);

    public void ObserveRateLimit(
        string accountPartition,
        int? remaining,
        DateTimeOffset? resetAt,
        TimeSpan? retryAfter = null,
        string? resource = null)
    {
    }

    public IReadOnlyList<AdaptivePrefetchCounter> GetCounters() => [];
}

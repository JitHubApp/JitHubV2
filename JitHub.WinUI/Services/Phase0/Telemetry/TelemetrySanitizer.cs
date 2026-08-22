using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace JitHub.Services;

public static class TelemetrySanitizer
{
    private static readonly HashSet<string> StoreEventAllowList = new(StringComparer.Ordinal)
    {
        "app.started",
        "shell.search.submitted",
        "shell.search.completed",
        "shell.nav.opened",
        "shell.route.opened",
        "shell.repo.selected",
        "shell.command.opened",
        "shell.command.executed",
        "shell.rail.refresh.completed",
        "dashboard.opened",
        "dashboard.refresh.started",
        "dashboard.refresh.completed",
        "dashboard.section.loaded",
        "dashboard.quick_action.executed",
        "dashboard.reconnect.clicked",
        "dashboard.customize.opened",
        "dashboard.customize.saved",
        "dashboard.customize.reset",
        "dashboard.widget.view_all.clicked",
        "dashboard.side_rail.opened",
        "dashboard.widget.toggled",
        "dialog.presentation.failed",
        "notifications.opened",
        "notifications.list.loaded",
        "notifications.filter.changed",
        "notifications.action.executed",
        "gists.opened",
        "gists.list.loaded",
        "gists.filter.changed",
        "gists.action.executed",
        "issues.opened",
        "issues.list.loaded",
        "issues.selected",
        "issues.prefetch.started",
        "issues.prefetch.completed",
        "issues.action.executed",
        "pull_requests.opened",
        "pull_requests.list.loaded",
        "pull_requests.selected",
        "pull_requests.section.opened",
        "pull_requests.prefetch.started",
        "pull_requests.prefetch.completed",
        "pull_requests.action.executed",
        "commits.opened",
        "commits.list.loaded",
        "commits.selected",
        "commits.filter.changed",
        "commits.section.opened",
        "commits.diff.mode.changed",
        "commits.compare.opened",
        "commits.prefetch.started",
        "commits.prefetch.completed",
        "commits.action.executed",
        "repo_code.opened",
        "repo_code.loaded",
        "repo_code.selected",
        "repo_code.action.executed",
        "repo_code.error",
        "repo_code.cache.observed",
        "repo_code.duration.recorded",
        "markdown.action.executed",
        "markdown.resource.unavailable",
        "markdown.error",
        "stars.opened",
        "stars.sync.completed",
        "stars.category.created",
        "stars.category.updated",
        "stars.category.deleted",
        "stars.membership.changed",
        "stars.filter.changed",
        "stars.sort.changed",
        "stars.action.executed",
        "repositories.opened",
        "repositories.sync.completed",
        "repositories.action.executed",
        "repository_search.opened",
        "repository_search.loaded",
        "repository_search.action.executed",
        "repository_search.error",
        "profile.opened",
        "profile.loaded",
        "profile.section.opened",
        "profile.action.executed",
        "profile.error",
        "settings.opened",
        "settings.loaded",
        "settings.action.executed",
        "settings.error",
        "auth.opened",
        "auth.flow.started",
        "auth.flow.completed",
        "auth.session.loaded",
        "auth.action.executed",
        "auth.error",
        "repository.action.executed",
        "github.cache.hit",
        "github.cache.miss",
        "github.cache.stale",
        "github.request.completed",
        "github.request.failed",
        "telemetry.metric"
    };

    private static readonly HashSet<string> SafePropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "cache_state",
        "duration_bucket",
        "error_kind",
        "event_kind",
        "feature",
        "http_status",
        "is_background",
        "metric",
        "page",
        "phase",
        "policy",
        "priority",
        "query_kind",
        "refresh",
        "resource",
        "result",
        "section",
        "source",
        "status",
        "action",
        "widget",
        "filter_type",
        "view_mode",
        "sort",
        "count_bucket"
    };

    private static readonly Regex UrlLikeValue = new(
        @"(?:https?://|github\.com/|[\w.-]+/[\w.-]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex TokenLikeValue = new(
        @"(?:gh[pousr]_[A-Za-z0-9_]{20,}|github_pat_[A-Za-z0-9_]{20,}|AKIA[A-Z0-9]{16}|Bearer\s+[A-Za-z0-9._~-]+|-----BEGIN\s+(?:RSA\s+)?PRIVATE\s+KEY-----|(?:password|passwd|secret|token|api[_-]?key)\s*[:=]\s*\S+|eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}|[A-Fa-f0-9]{32,}|[A-Za-z0-9+/]{40,}={0,2})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex CountBucketValue = new(
        @"^(?:0|1|[0-9]+_[0-9]+|lt_[0-9]+|gt_[0-9]+|gte_[0-9]+|[0-9]+_plus)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex HttpStatusValue = new(
        @"^[1-5][0-9]{2}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Dictionary<string, string> CanonicalAliases = new(StringComparer.Ordinal)
    {
        ["canceled"] = "cancelled",
        ["failure"] = "failed",
        ["under_50ms"] = "lt_50ms",
        ["50_149ms"] = "lt_150ms",
        ["150_499ms"] = "lt_500ms",
        ["500_1999ms"] = "lt_3s",
        ["2000ms_plus"] = "gte_3s"
    };

    public static bool IsStoreEventAllowed(string name) => StoreEventAllowList.Contains(name);

    internal static IReadOnlyCollection<string> GetAllowedStoreEvents() => StoreEventAllowList.ToArray();

    public static string NormalizeEventName(string name)
    {
        string normalized = string.IsNullOrWhiteSpace(name)
            ? "telemetry.unknown"
            : name.Trim().ToLowerInvariant();

        return StoreEventAllowList.Contains(normalized)
            ? normalized
            : "telemetry.unknown";
    }

    public static IReadOnlyDictionary<string, string> SanitizeProperties(
        IReadOnlyDictionary<string, string?>? properties)
    {
        if (properties is null || properties.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        Dictionary<string, string> sanitized = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, string? value) in properties)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            string normalizedKey = key.Trim().ToLowerInvariant();
            string normalizedValue = NormalizePropertyValue(normalizedKey, value);
            if (!IsSafeKey(normalizedKey) || !IsSafeValue(normalizedKey, normalizedValue))
            {
                continue;
            }

            sanitized[normalizedKey] = normalizedValue.Length <= 80
                ? normalizedValue
                : normalizedValue[..80];
        }

        return sanitized;
    }

    public static string CreateDurationBucket(TimeSpan duration)
    {
        double milliseconds = duration.TotalMilliseconds;
        if (milliseconds < 50)
        {
            return "lt_50ms";
        }

        if (milliseconds < 150)
        {
            return "lt_150ms";
        }

        if (milliseconds < 500)
        {
            return "lt_500ms";
        }

        if (milliseconds < 1000)
        {
            return "lt_1s";
        }

        if (milliseconds < 3000)
        {
            return "lt_3s";
        }

        return "gte_3s";
    }

    private static bool IsSafeKey(string key) => SafePropertyNames.Contains(key);

    private static string NormalizePropertyValue(string key, string value)
    {
        string normalized = Regex.Replace(
                value.Trim(),
                @"(?<=[a-z0-9])(?=[A-Z])",
                "_",
                RegexOptions.CultureInvariant)
            .Replace('-', '_')
            .Replace(' ', '_')
            .ToLowerInvariant();

        if (key == "error_kind")
        {
            normalized = normalized switch
            {
                "github_rate_limit_exception" => "rate_limit",
                "github_authentication_exception" or "unauthorized_access_exception" => "authentication",
                "http_request_exception" => "network",
                "task_canceled_exception" or "operation_canceled_exception" => "cancelled",
                "invalid_operation_exception" => "invalid_operation",
                "io_exception" => "io",
                _ => normalized
            };
        }

        return CanonicalAliases.TryGetValue(normalized, out string? canonical)
            ? canonical
            : normalized;
    }

    private static bool IsSafeValue(string key, string value)
    {
        if (value.Length is 0 or > 80 || UrlLikeValue.IsMatch(value) || TokenLikeValue.IsMatch(value))
        {
            return false;
        }

        return key switch
        {
            "duration_bucket" => value is "lt_50ms" or "lt_150ms" or "lt_500ms" or "lt_1s" or "lt_3s" or "gte_3s",
            "http_status" => HttpStatusValue.IsMatch(value),
            "count_bucket" => CountBucketValue.IsMatch(value),
            "is_background" or "refresh" => value is "true" or "false",
            "metric" => value == "prefetch.policy.decision",
            _ => TelemetryTaxonomy.EmitterValueCatalog.TryGetValue(key, out IReadOnlyCollection<string>? values) &&
                 values.Contains(value)
        };
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models;
using JitHub.Services;
using JitHub.Services.CodeViewer;
using JitHub.WinUI.Tests.TestDoubles;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class Phase0TelemetryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "JitHubPhase0TelemetryTests", Guid.NewGuid().ToString());

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public void SanitizeProperties_DropsRepoUrlsTokensAndTitles()
    {
        IReadOnlyDictionary<string, string> properties = TelemetrySanitizer.SanitizeProperties(
            new Dictionary<string, string?>
            {
                ["resource"] = "search",
                ["cache_state"] = "Fresh",
                ["repository"] = "owner/repo",
                ["title"] = "Secret issue title",
                ["url"] = "https://github.com/owner/repo",
                ["token"] = "ghp_abcdefghijklmnopqrstuvwxyz123456"
            });

        Assert.Equal("search", properties["resource"]);
        Assert.Equal("fresh", properties["cache_state"]);
        Assert.False(properties.ContainsKey("repository"));
        Assert.False(properties.ContainsKey("title"));
        Assert.False(properties.ContainsKey("url"));
        Assert.False(properties.ContainsKey("token"));
    }

    [Fact]
    public void StoreEventAllowlist_AllowsOnlyCanonicalEvents()
    {
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("app.background_task.failed"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("app.exception.handled"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("app.exception.unhandled"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("shell.search.submitted"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("shell.nav.opened"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("shell.route.opened"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("shell.repo.selected"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("shell.command.opened"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("shell.command.executed"));
        Assert.False(TelemetrySanitizer.IsStoreEventAllowed("shell.tab.created"));
        Assert.False(TelemetrySanitizer.IsStoreEventAllowed("shell.tab.selected"));
        Assert.False(TelemetrySanitizer.IsStoreEventAllowed("shell.tab.closed"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("shell.rail.refresh.completed"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("dashboard.opened"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("dashboard.refresh.started"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("dashboard.refresh.completed"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("dashboard.section.loaded"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("dashboard.quick_action.executed"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("dashboard.reconnect.clicked"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("notifications.opened"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("notifications.list.loaded"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("notifications.filter.changed"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("notifications.action.executed"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("issues.filter.changed"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("pull_requests.opened"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("pull_requests.list.loaded"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("pull_requests.filter.changed"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("pull_requests.selected"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("pull_requests.section.opened"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("pull_requests.prefetch.started"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("pull_requests.prefetch.completed"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("pull_requests.action.executed"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("commits.opened"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("commits.list.loaded"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("commits.selected"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("commits.filter.changed"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("commits.section.opened"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("commits.diff.mode.changed"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("commits.diff.prepared"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("commits.compare.opened"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("commits.compare.refs_swapped"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("commits.prefetch.started"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("commits.prefetch.completed"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("commits.action.executed"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("repo_code.opened"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("repo_code.loaded"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("repo_code.selected"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("repo_code.action.executed"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("repo_code.error"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("repo_code.cache.observed"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("repo_code.duration.recorded"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("stars.opened"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("stars.sync.completed"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("stars.category.created"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("stars.membership.changed"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("stars.filter.changed"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("stars.sort.changed"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("stars.action.executed"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("repositories.opened"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("repositories.sync.completed"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("repositories.action.executed"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("repository_search.opened"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("repository_search.loaded"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("repository_search.action.executed"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("repository_search.error"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("profile.opened"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("profile.loaded"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("profile.section.opened"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("profile.action.executed"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("profile.error"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("settings.opened"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("settings.loaded"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("settings.action.executed"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("settings.error"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("auth.opened"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("auth.flow.started"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("auth.flow.completed"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("auth.session.loaded"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("auth.action.executed"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("auth.error"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed("repository.action.executed"));
        Assert.True(TelemetrySanitizer.IsStoreEventAllowed(
            "p.repo_code.action.executed.a.csv_sort.r.success.src.action"));
        Assert.False(TelemetrySanitizer.IsStoreEventAllowed(
            "p.repo_code.action.executed.a.owner_private_repo.r.success"));
        Assert.False(TelemetrySanitizer.IsStoreEventAllowed("shell.search.submitted.owner.repo"));
        Assert.Equal("telemetry.unknown", TelemetrySanitizer.NormalizeEventName("repo/opened/owner/repo"));
    }

    [Fact]
    public async Task BackgroundTaskObserver_ContainsCancellationAndReportsUnexpectedFailure()
    {
        RecordingTelemetryService telemetry = new();

        await BackgroundTaskObserver.ObserveAsync(
            Task.FromCanceled(new CancellationToken(canceled: true)),
            "dashboard",
            telemetry);
        Assert.Empty(telemetry.Events);

        InvalidOperationException failure = new("test failure");
        Exception? observedFailure = null;
        await BackgroundTaskObserver.ObserveAsync(
            Task.FromException(failure),
            "dashboard",
            telemetry,
            exception => observedFailure = exception);

        Assert.Same(failure, observedFailure);
        RecordedTelemetryEvent recorded = Assert.Single(telemetry.Events);
        Assert.Equal("app.background_task.failed", recorded.Name);
        Assert.Equal("dashboard", recorded.Properties["feature"]);
        Assert.Equal("InvalidOperationException", recorded.Properties["error_kind"]);
        Assert.Equal("background", recorded.Properties["phase"]);
    }

    [Fact]
    public void SanitizeProperties_RejectsValuesRegisteredForADifferentDimension()
    {
        IReadOnlyDictionary<string, string> properties = TelemetrySanitizer.SanitizeProperties(
            new Dictionary<string, string?>
            {
                ["page"] = TelemetryTaxonomy.Results.Success,
                ["action"] = TelemetryTaxonomy.Sources.Navigation,
                ["result"] = TelemetryTaxonomy.Actions.OpenRepository,
                ["source"] = TelemetryTaxonomy.Results.Failed,
                ["section"] = "overview"
            });

        Assert.Single(properties);
        Assert.Equal("overview", properties["section"]);
    }

    [Fact]
    public void StoreEventAllowlist_MatchesEmittedLiteralTaxonomy()
    {
        string productRoot = Path.Combine(FindRepositoryRoot(), "JitHub.WinUI");
        HashSet<string> emittedEvents = Directory.EnumerateFiles(productRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.EndsWith("TelemetrySanitizer.cs", StringComparison.OrdinalIgnoreCase))
            .SelectMany(path => ExtractTelemetryEventLiterals(File.ReadAllText(path)))
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> allowedEvents = TelemetrySanitizer.GetAllowedStoreEvents()
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(
            allowedEvents.OrderBy(static value => value, StringComparer.Ordinal),
            emittedEvents.OrderBy(static value => value, StringComparer.Ordinal));
    }

    [Fact]
    public void SanitizeProperties_AllowsCommitCategoriesAndDropsIdentifiers()
    {
        IReadOnlyDictionary<string, string> properties = TelemetrySanitizer.SanitizeProperties(
            new Dictionary<string, string?>
            {
                ["page"] = "repo",
                ["section"] = "diff",
                ["filter_type"] = "author",
                ["view_mode"] = "split",
                ["branch"] = "feature/native-diff",
                ["sha"] = "3f9a1c2abcdef",
                ["query"] = "fix impeller",
                ["path"] = "src/app.cs"
            });

        Assert.Equal("repo", properties["page"]);
        Assert.Equal("diff", properties["section"]);
        Assert.Equal("author", properties["filter_type"]);
        Assert.Equal("split", properties["view_mode"]);
        Assert.False(properties.ContainsKey("branch"));
        Assert.False(properties.ContainsKey("sha"));
        Assert.False(properties.ContainsKey("query"));
        Assert.False(properties.ContainsKey("path"));
    }

    [Fact]
    public void SanitizeProperties_NewRouteTelemetryRejectsQueriesCallbacksAndIdentity()
    {
        IReadOnlyDictionary<string, string> properties = TelemetrySanitizer.SanitizeProperties(
            new Dictionary<string, string?>
            {
                ["page"] = "repository_search",
                ["source"] = "route",
                ["action"] = "search",
                ["result"] = "success",
                ["duration_bucket"] = "lt_150ms",
                ["query"] = "private repository query",
                ["callback"] = "token=ghp_abcdefghijklmnopqrstuvwxyz123456&state=secret",
                ["username"] = "private-user",
                ["repository"] = "private-owner/private-repo",
                ["path"] = "C:\\Users\\private\\diagnostics.ndjson"
            });

        Assert.Equal("repository_search", properties["page"]);
        Assert.Equal("route", properties["source"]);
        Assert.Equal("search", properties["action"]);
        Assert.Equal("success", properties["result"]);
        Assert.Equal("lt_150ms", properties["duration_bucket"]);
        Assert.DoesNotContain("query", properties.Keys);
        Assert.DoesNotContain("callback", properties.Keys);
        Assert.DoesNotContain("username", properties.Keys);
        Assert.DoesNotContain("repository", properties.Keys);
        Assert.DoesNotContain("path", properties.Keys);
    }

    [Fact]
    public void SanitizeProperties_RejectsArbitraryTextEvenUnderRegisteredKeys()
    {
        IReadOnlyDictionary<string, string> properties = TelemetrySanitizer.SanitizeProperties(
            new Dictionary<string, string?>
            {
                ["source"] = "private_user",
                ["action"] = "secret_repository",
                ["section"] = "issue_185570",
                ["result"] = "Success",
                ["cache_state"] = "Fresh",
                ["duration_bucket"] = "under_50ms"
            });

        Assert.DoesNotContain("source", properties.Keys);
        Assert.DoesNotContain("action", properties.Keys);
        Assert.DoesNotContain("section", properties.Keys);
        Assert.Equal("success", properties["result"]);
        Assert.Equal("fresh", properties["cache_state"]);
        Assert.Equal("lt_50ms", properties["duration_bucket"]);
    }

    [Fact]
    public void EmitterTaxonomy_EveryRegisteredDimensionRoundTripsThroughSanitizer()
    {
        foreach ((string key, IReadOnlyCollection<string> values) in TelemetryTaxonomy.EmitterValueCatalog)
        {
            foreach (string value in values)
            {
                IReadOnlyDictionary<string, string> sanitized = TelemetrySanitizer.SanitizeProperties(
                    new Dictionary<string, string?> { [key] = value });

                Assert.True(
                    sanitized.TryGetValue(key, out string? actual),
                    $"Telemetry dimension '{key}={value}' was silently dropped.");
                Assert.Equal(value, actual);
            }
        }

        foreach (int count in new[] { 0, 1, 2, 10, 11, 50, 51, 200, 201, int.MaxValue })
        {
            string bucket = TelemetryTaxonomy.CountBucket(count);
            IReadOnlyDictionary<string, string> sanitized = TelemetrySanitizer.SanitizeProperties(
                new Dictionary<string, string?> { ["count_bucket"] = bucket });
            Assert.Equal(bucket, sanitized["count_bucket"]);
        }
    }

    [Fact]
    public void AdaptivePrefetchTelemetry_EveryEnumDimensionSurvivesSanitization()
    {
        foreach (AdaptivePrefetchFeature feature in Enum.GetValues<AdaptivePrefetchFeature>())
        {
            AssertDimensionRoundTrips("feature", [TelemetryTaxonomy.EnumValue(feature)]);
        }

        foreach (AdaptivePrefetchStage stage in Enum.GetValues<AdaptivePrefetchStage>())
        {
            AssertDimensionRoundTrips("phase", [TelemetryTaxonomy.EnumValue(stage)]);
        }

        foreach (AdaptivePrefetchSuppressionReason reason in Enum.GetValues<AdaptivePrefetchSuppressionReason>())
        {
            AssertDimensionRoundTrips("source", [TelemetryTaxonomy.EnumValue(reason)]);
        }

        AssertDimensionRoundTrips("result", ["allowed", "suppressed"]);
    }

    [Fact]
    public void WorkspaceSectionAndNavigationReasons_SurviveWithoutRelaxingContentRules()
    {
        AssertDimensionRoundTrips("section", ["comments", "checks", "compare", "files"]);
        AssertDimensionRoundTrips("source", [TelemetryTaxonomy.Sources.NavigationHandoff]);

        Assert.Empty(TelemetrySanitizer.SanitizeProperties(
            new Dictionary<string, string?>
            {
                ["section"] = "private issue title",
                ["source"] = "owner/private-repository"
            }));
    }

    [Fact]
    public void AffectedFeatureTaxonomy_PreservesCanonicalValuesAndStillRejectsContent()
    {
        AssertDimensionRoundTrips("result",
        [
            TelemetryTaxonomy.Results.Success,
            TelemetryTaxonomy.Results.Partial,
            TelemetryTaxonomy.Results.Error,
            TelemetryTaxonomy.Results.AuthError,
            TelemetryTaxonomy.Results.Cancelled,
            TelemetryTaxonomy.Results.CachedError,
            TelemetryTaxonomy.Results.NetworkError,
            TelemetryTaxonomy.Results.PermissionDenied,
            TelemetryTaxonomy.Results.Rejected,
            TelemetryTaxonomy.Results.Enabled,
            TelemetryTaxonomy.Results.Disabled,
            TelemetryTaxonomy.Results.Queued,
            TelemetryTaxonomy.Results.Deferred
        ]);
        AssertDimensionRoundTrips("source",
        [
            TelemetryTaxonomy.Sources.Action,
            TelemetryTaxonomy.Sources.Cache,
            TelemetryTaxonomy.Sources.Dialog,
            TelemetryTaxonomy.Sources.Dwell,
            TelemetryTaxonomy.Sources.Full,
            TelemetryTaxonomy.Sources.Hover,
            TelemetryTaxonomy.Sources.Incremental,
            TelemetryTaxonomy.Sources.List,
            TelemetryTaxonomy.Sources.Login,
            TelemetryTaxonomy.Sources.Navigation,
            TelemetryTaxonomy.Sources.NavigationHandoff,
            TelemetryTaxonomy.Sources.Neighbor,
            TelemetryTaxonomy.Sources.Notifications,
            TelemetryTaxonomy.Sources.Refresh,
            TelemetryTaxonomy.Sources.Route,
            TelemetryTaxonomy.Sources.Shell
        ]);
        AssertDimensionRoundTrips("filter_type",
        [
            TelemetryTaxonomy.FilterTypes.All,
            TelemetryTaxonomy.FilterTypes.Participating,
            TelemetryTaxonomy.FilterTypes.Unread
        ]);
        AssertDimensionRoundTrips("action",
        [
            TelemetryTaxonomy.Actions.Add,
            TelemetryTaxonomy.Actions.Remove,
            TelemetryTaxonomy.Actions.Reorder,
            TelemetryTaxonomy.Actions.ReviewApprove,
            TelemetryTaxonomy.Actions.ReviewComment,
            TelemetryTaxonomy.Actions.ReviewReply,
            TelemetryTaxonomy.Actions.ReviewRequestChanges,
            TelemetryTaxonomy.Actions.ClearAllCache,
            TelemetryTaxonomy.Actions.ClearDiagnostics,
            TelemetryTaxonomy.Actions.ClearImageCache,
            TelemetryTaxonomy.Actions.ClearQueryCache,
            TelemetryTaxonomy.Actions.ClearRepoFileCache,
            TelemetryTaxonomy.Actions.ClearStarsLibrary,
            TelemetryTaxonomy.Actions.ExportDiagnostics,
            TelemetryTaxonomy.Actions.Diagnostics,
            TelemetryTaxonomy.Actions.StoreTelemetry,
            RepoCodeTelemetryActions.Find,
            RepoCodeTelemetryActions.Outline,
            RepoCodeTelemetryActions.CopyPath,
            RepoCodeTelemetryActions.CopyRaw,
            RepoCodeTelemetryActions.CopyLineLink,
            RepoCodeTelemetryActions.Drawer,
            RepoCodeTelemetryActions.ExternalOpen,
            RepoCodeTelemetryActions.BreadcrumbRoot,
            RepoCodeTelemetryActions.BreadcrumbPath,
            RepoCodeTelemetryActions.CsvCopy,
            RepoCodeTelemetryActions.CsvPlainView,
            RepoCodeTelemetryActions.CsvReorder,
            RepoCodeTelemetryActions.CsvResize,
            RepoCodeTelemetryActions.CsvRichView,
            RepoCodeTelemetryActions.CsvSort,
            RepoCodeTelemetryActions.ImageZoom,
            RepoCodeTelemetryActions.JsonPlainView,
            RepoCodeTelemetryActions.JsonRichView,
            RepoCodeTelemetryActions.SvgZoom,
            RepoCodeTelemetryActions.XmlPlainView,
            RepoCodeTelemetryActions.XmlRichView
        ]);

        IReadOnlyDictionary<string, string> sanitized = TelemetrySanitizer.SanitizeProperties(
            new Dictionary<string, string?>
            {
                ["action"] = "owner/repository",
                ["source"] = "https://github.com/private-user",
                ["result"] = "issue title body",
                ["filter_type"] = "secret-topic-name",
                ["repository"] = "owner/repository",
                ["username"] = "private-user",
                ["query"] = "private query",
                ["token"] = "github_pat_abcdefghijklmnopqrstuvwxyz1234567890"
            });

        Assert.Empty(sanitized);
    }

    [Fact]
    public void NavigationResult_ReportsSuccessOnlyForAcceptedNavigation()
    {
        Assert.Equal(
            TelemetryTaxonomy.Results.Success,
            TelemetryTaxonomy.NavigationResult(accepted: true));
        Assert.Equal(
            TelemetryTaxonomy.Results.Rejected,
            TelemetryTaxonomy.NavigationResult(accepted: false));
    }

    private static void AssertDimensionRoundTrips(string key, IEnumerable<string> values)
    {
        foreach (string value in values.Distinct(StringComparer.Ordinal))
        {
            IReadOnlyDictionary<string, string> sanitized = TelemetrySanitizer.SanitizeProperties(
                new Dictionary<string, string?> { [key] = value });
            Assert.Equal(value, sanitized[key]);
        }
    }

    [Fact]
    public void SafeTelemetryService_ContainsArbitraryImplementationAndTraceFailures()
    {
        ITelemetryService telemetry = SafeTelemetryService.Wrap(new ThrowingTelemetryService());

        telemetry.TrackEvent("dashboard.opened");
        telemetry.TrackMetric("prefetch.policy.decision", 1);
        using IPerformanceTrace trace = telemetry.StartTrace("profile.loaded");
        trace.SetProperty("result", "success");
    }

    [Fact]
    public void TelemetryService_ProjectsSanitizedDimensionsForPartnerCenter()
    {
        NonBlockingDiagnosticsStore store = new();
        CountingStoreTelemetrySink sink = new(isAvailable: true);
        TelemetryService telemetry = new(store, sink, new MemorySettingService());

        telemetry.TrackEvent(
            "repo_code.action.executed",
            new Dictionary<string, string?>
            {
                ["action"] = "csv_sort",
                ["result"] = "success",
                ["source"] = "action",
                ["path"] = "private/repository/data.csv"
            });

        Assert.Equal(
            [
                "repo_code.action.executed",
                "p.repo_code.action.executed.a.csv_sort.r.success.src.action"
            ],
            sink.Events);
    }

    [Fact]
    public void ThemePaletteTelemetry_ProjectsTheSelectedFamilyForPartnerCenter()
    {
        NonBlockingDiagnosticsStore store = new();
        CountingStoreTelemetrySink sink = new(isAvailable: true);
        TelemetryService telemetry = new(store, sink, new MemorySettingService());

        telemetry.TrackEvent(
            "settings.action.executed",
            new Dictionary<string, string?>
            {
                ["action"] = TelemetryTaxonomy.Actions.ThemePaletteChanged,
                ["theme_palette"] = ThemePaletteIds.VisualStudioCode,
                ["result"] = TelemetryTaxonomy.Results.Success
            });

        Assert.Equal(
            [
                "settings.action.executed",
                "p.settings.action.executed.a.theme_palette_changed.r.success.tp.visual-studio-code"
            ],
            sink.Events);
    }

    [Theory]
    [InlineData("ui-repo-pull-request-page", "pull_requests")]
    [InlineData("ui-repo-file-tree-presentation", "code")]
    [InlineData("content-dialog-presentation", "ui")]
    [InlineData("task-unobserved", "runtime")]
    [InlineData("diagnostics-shutdown", "telemetry")]
    public void ExceptionCategoryClassifier_ProducesBoundedStoreFeatures(string category, string expected)
    {
        Assert.Equal(expected, TelemetryTaxonomy.FeatureForExceptionCategory(category));
        AssertDimensionRoundTrips("feature", [expected]);
    }

    [Fact]
    public void TelemetryService_ProjectsExceptionFeatureForPartnerCenter()
    {
        NonBlockingDiagnosticsStore store = new();
        CountingStoreTelemetrySink sink = new(isAvailable: true);
        TelemetryService telemetry = new(store, sink, new MemorySettingService());

        telemetry.TrackEvent(
            "app.exception.handled",
            new Dictionary<string, string?>
            {
                ["error_kind"] = "XamlParseException",
                ["feature"] = "code"
            });

        Assert.Equal(
            [
                "app.exception.handled",
                "p.app.exception.handled.e.unexpected.ft.code"
            ],
            sink.Events);
    }

    private static IEnumerable<string> ExtractTelemetryEventLiterals(string source)
    {
        string[] methodNames = ["TrackEvent", "TrackEventSafely", "TrackStoreEvent", "TrackCategory", "TrackCacheEvent", "TrackMarkdownEvent", "TrackExceptionTelemetry", "StartTrace"];
        foreach (string methodName in methodNames)
        {
            int searchStart = 0;
            while ((searchStart = source.IndexOf(methodName, searchStart, StringComparison.Ordinal)) >= 0)
            {
                int openParenthesis = searchStart + methodName.Length;
                while (openParenthesis < source.Length && char.IsWhiteSpace(source[openParenthesis]))
                {
                    openParenthesis++;
                }

                if (openParenthesis >= source.Length || source[openParenthesis] != '(')
                {
                    searchStart += methodName.Length;
                    continue;
                }

                string firstArgument = ReadFirstArgument(source, openParenthesis + 1);
                foreach (Match match in Regex.Matches(
                             firstArgument,
                             "\"(?<event>[a-z][a-z0-9_]*(?:\\.[a-z0-9_]+)+)\"",
                             RegexOptions.CultureInvariant))
                {
                    yield return match.Groups["event"].Value;
                }

                searchStart = openParenthesis + 1;
            }
        }
    }

    private static string ReadFirstArgument(string source, int start)
    {
        bool inString = false;
        bool escaping = false;
        int nestedParentheses = 0;
        for (int index = start; index < source.Length; index++)
        {
            char current = source[index];
            if (inString)
            {
                if (escaping)
                {
                    escaping = false;
                }
                else if (current == '\\')
                {
                    escaping = true;
                }
                else if (current == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (current == '"')
            {
                inString = true;
            }
            else if (current == '(')
            {
                nestedParentheses++;
            }
            else if (current == ')')
            {
                if (nestedParentheses == 0)
                {
                    return source[start..index];
                }

                nestedParentheses--;
            }
            else if (current == ',' && nestedParentheses == 0)
            {
                return source[start..index];
            }
        }

        return source[start..];
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")) &&
                Directory.Exists(Path.Combine(directory.FullName, "JitHub.WinUI")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the JitHub repository root.");
    }

    [Fact]
    public void StoreTelemetrySink_ReportsAvailabilityAndRejectsDynamicEvents()
    {
        StoreTelemetrySink sink = new();

        Assert.False(string.IsNullOrWhiteSpace(sink.AvailabilityStatus));
        sink.TrackEvent("shell.search.submitted");
        sink.TrackEvent("shell.search.submitted.owner.repo");
    }

    [Fact]
    public async Task StoreTelemetrySink_PacesNativeLoggerCallsOffTheCallerThread()
    {
        List<long> dispatchTimes = [];
        object dispatchGate = new();
        Stopwatch stopwatch = Stopwatch.StartNew();
        StoreTelemetrySink sink = new(
            name =>
            {
                lock (dispatchGate)
                {
                    dispatchTimes.Add(stopwatch.ElapsedMilliseconds);
                }
            },
            TimeSpan.FromMilliseconds(60),
            queueCapacity: 4);

        sink.TrackEvent("app.started");
        sink.TrackEvent("shell.route.opened");
        sink.TrackEvent("dashboard.opened");

        Assert.True(await sink.WaitForIdleAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(3, dispatchTimes.Count);
        Assert.True(dispatchTimes[1] - dispatchTimes[0] >= 40);
        Assert.True(dispatchTimes[2] - dispatchTimes[1] >= 40);
    }

    [Fact]
    public async Task StoreTelemetrySink_CoalescesPendingNamesAndBoundsStartupBursts()
    {
        using ManualResetEventSlim firstDispatchStarted = new();
        using ManualResetEventSlim releaseFirstDispatch = new();
        List<string> dispatched = [];
        object dispatchGate = new();
        StoreTelemetrySink sink = new(
            name =>
            {
                lock (dispatchGate)
                {
                    dispatched.Add(name);
                }

                if (name == "app.started")
                {
                    firstDispatchStarted.Set();
                    Assert.True(releaseFirstDispatch.Wait(TimeSpan.FromSeconds(2)));
                }
            },
            TimeSpan.Zero,
            queueCapacity: 2);

        sink.TrackEvent("app.started");
        Assert.True(firstDispatchStarted.Wait(TimeSpan.FromSeconds(2)));
        sink.TrackEvent("shell.route.opened");
        sink.TrackEvent("shell.route.opened");
        sink.TrackEvent("dashboard.opened");
        sink.TrackEvent("dashboard.refresh.completed");

        Assert.Equal(1, sink.CoalescedEventCount);
        Assert.Equal(1, sink.DroppedEventCount);
        Assert.Equal(3, sink.PendingEventCount);

        releaseFirstDispatch.Set();
        Assert.True(await sink.WaitForIdleAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(
            ["app.started", "shell.route.opened", "dashboard.opened"],
            dispatched);
    }

    [Fact]
    public async Task LocalDiagnosticsStore_AppendsReadsAndClearsEvents()
    {
        string path = Path.Combine(_root, "diagnostics.ndjson");
        await using LocalDiagnosticsStore store = new(path, maxBytes: 1024 * 1024, TimeSpan.FromDays(14));
        LocalDiagnosticEvent entry = new(
            DateTimeOffset.UtcNow,
            "event",
            "shell.search.submitted",
            new Dictionary<string, string> { ["resource"] = "search" });

        await store.AppendAsync(entry);

        IReadOnlyList<LocalDiagnosticEvent> entries = await store.ReadAsync();
        Assert.Single(entries);
        Assert.Equal("shell.search.submitted", entries[0].Name);
        Assert.True(await store.GetSizeAsync() > 0);

        await store.ClearAsync();
        Assert.Empty(await store.ReadAsync());
    }

    [Fact]
    public async Task LocalDiagnosticsStore_PreservesQueuedAppendOrder()
    {
        string path = Path.Combine(_root, "diagnostics-order.ndjson");
        await using LocalDiagnosticsStore store = new(
            path,
            maxBytes: 1024 * 1024,
            TimeSpan.FromDays(14),
            queueCapacity: 4);

        Task[] appends = Enumerable.Range(0, 64)
            .Select(index => store.AppendAsync(CreateDiagnostic($"ordered-{index:D2}")))
            .ToArray();

        await Task.WhenAll(appends).WaitAsync(TimeSpan.FromSeconds(5));
        await store.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));

        IReadOnlyList<LocalDiagnosticEvent> entries = await store.ReadAsync();
        Assert.Equal(
            Enumerable.Range(0, 64).Select(index => $"ordered-{index:D2}"),
            entries.Select(static entry => entry.Name));
    }

    [Fact]
    public async Task LocalDiagnosticsStore_ConcurrentAppendsRemainCompleteAndOrderedPerProducer()
    {
        string path = Path.Combine(_root, "diagnostics-concurrent.ndjson");
        await using LocalDiagnosticsStore store = new(
            path,
            maxBytes: 4 * 1024 * 1024,
            TimeSpan.FromDays(14),
            queueCapacity: 8);

        const int producerCount = 8;
        const int eventsPerProducer = 40;
        Task[] producers = Enumerable.Range(0, producerCount)
            .Select(async producer =>
            {
                for (int sequence = 0; sequence < eventsPerProducer; sequence++)
                {
                    await store.AppendAsync(new LocalDiagnosticEvent(
                        DateTimeOffset.UtcNow,
                        "event",
                        "concurrent",
                        new Dictionary<string, string>
                        {
                            ["producer"] = producer.ToString(),
                            ["sequence"] = sequence.ToString()
                        }));
                }
            })
            .ToArray();

        await Task.WhenAll(producers).WaitAsync(TimeSpan.FromSeconds(10));
        IReadOnlyList<LocalDiagnosticEvent> entries = await store.ReadAsync();

        Assert.Equal(producerCount * eventsPerProducer, entries.Count);
        for (int producer = 0; producer < producerCount; producer++)
        {
            int[] sequences = entries
                .Where(entry => entry.Properties["producer"] == producer.ToString())
                .Select(entry => int.Parse(entry.Properties["sequence"]))
                .ToArray();
            Assert.Equal(Enumerable.Range(0, eventsPerProducer), sequences);
        }
    }

    [Fact]
    public async Task LocalDiagnosticsStore_FlushIsAnOrderedBarrierForAcceptedNonBlockingAppends()
    {
        string path = Path.Combine(_root, "diagnostics-flush.ndjson");
        await using LocalDiagnosticsStore store = new(
            path,
            maxBytes: 4 * 1024 * 1024,
            TimeSpan.FromDays(14),
            queueCapacity: 128);

        bool[] accepted = Enumerable.Range(0, 96)
            .Select(index => store.TryAppend(CreateDiagnostic($"flush-{index:D2}")))
            .ToArray();

        Assert.All(accepted, Assert.True);
        await store.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));

        string[] lines = await File.ReadAllLinesAsync(path);
        Assert.Equal(96, lines.Length);
        Assert.Equal(
            Enumerable.Range(0, 96).Select(index => $"flush-{index:D2}"),
            lines.Select(line => JsonSerializer.Deserialize(
                line,
                LocalDiagnosticsJsonContext.Default.DiagnosticEvent)!.Name));
    }

    [Fact]
    public async Task LocalDiagnosticsStore_SaturationCountsDropsAndWritesCoalescedOverflowSignal()
    {
        string path = Path.Combine(_root, "diagnostics-overflow.ndjson");
        await using LocalDiagnosticsStore store = new(
            path,
            maxBytes: 8 * 1024 * 1024,
            TimeSpan.FromDays(14),
            queueCapacity: 1);

        int rejected = 0;
        for (int index = 0; index < 20_000; index++)
        {
            if (!store.TryAppend(CreateDiagnostic($"saturation-{index:D5}")))
            {
                rejected++;
            }
        }

        await store.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10));
        IReadOnlyList<LocalDiagnosticEvent> entries = await store.ReadAsync();
        LocalDiagnosticEvent[] overflowSignals = entries
            .Where(static entry => entry.Name == "diagnostics.queue.overflow")
            .ToArray();
        long signaledDrops = overflowSignals.Sum(static entry =>
            long.Parse(entry.Properties["dropped_count"], System.Globalization.CultureInfo.InvariantCulture));

        Assert.True(rejected > 0);
        Assert.Equal(rejected, store.DroppedEventCount);
        Assert.Equal(store.DroppedEventCount, signaledDrops);
        Assert.InRange(overflowSignals.Length, 1, rejected);
    }

    [Fact]
    public async Task LocalDiagnosticsStore_ShutdownDrainsAcceptedOperationsAndRejectsNewOnes()
    {
        string path = Path.Combine(_root, "diagnostics-shutdown.ndjson");
        await using LocalDiagnosticsStore store = new(
            path,
            maxBytes: 4 * 1024 * 1024,
            TimeSpan.FromDays(14),
            queueCapacity: 2);

        Task[] appends = Enumerable.Range(0, 100)
            .Select(index => store.AppendAsync(CreateDiagnostic($"shutdown-{index:D3}")))
            .ToArray();

        await store.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await Task.WhenAll(appends).WaitAsync(TimeSpan.FromSeconds(2));
        await store.FlushAsync().WaitAsync(TimeSpan.FromSeconds(2));

        string[] lines = await File.ReadAllLinesAsync(path);
        Assert.Equal(100, lines.Length);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => store.AppendAsync(CreateDiagnostic("after-shutdown")));
    }

    [Fact]
    public async Task LocalDiagnosticsStore_ShutdownAndFlushAreIdempotent()
    {
        string path = Path.Combine(_root, "diagnostics-idempotent-shutdown.ndjson");
        LocalDiagnosticsStore store = new(path, 1024 * 1024, TimeSpan.FromDays(14));
        Assert.True(store.TryAppend(CreateDiagnostic("before-shutdown")));

        await store.ShutdownAsync();
        await store.ShutdownAsync();
        await store.FlushAsync();
        await store.DisposeAsync();

        Assert.False(store.TryAppend(CreateDiagnostic("after-shutdown")));
        Assert.Single(await File.ReadAllLinesAsync(path));
    }

    [Fact]
    public async Task LocalDiagnosticsStore_CanceledOperationDoesNotPoisonWriter()
    {
        string path = Path.Combine(_root, "diagnostics-cancellation.ndjson");
        await using LocalDiagnosticsStore store = new(path, 1024 * 1024, TimeSpan.FromDays(14));
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.AppendAsync(CreateDiagnostic("canceled"), cancellation.Token));

        await store.AppendAsync(CreateDiagnostic("after-cancellation"));
        LocalDiagnosticEvent entry = Assert.Single(await store.ReadAsync());
        Assert.Equal("after-cancellation", entry.Name);
    }

    [Fact]
    public async Task LocalDiagnosticsStore_TrimsExpiredAndOldestRowsWithinByteCap()
    {
        string path = Path.Combine(_root, "diagnostics-trim.ndjson");
        const long maxBytes = 700;
        await using LocalDiagnosticsStore store = new(
            path,
            maxBytes,
            TimeSpan.FromDays(14),
            queueCapacity: 4);

        await store.AppendAsync(CreateDiagnostic(
            "expired",
            timestamp: DateTimeOffset.UtcNow.Subtract(TimeSpan.FromDays(15))));
        for (int index = 0; index < 24; index++)
        {
            await store.AppendAsync(new LocalDiagnosticEvent(
                DateTimeOffset.UtcNow,
                "event",
                $"retained-{index:D2}",
                new Dictionary<string, string> { ["result"] = new string('x', 48) }));
        }

        IReadOnlyList<LocalDiagnosticEvent> entries = await store.ReadAsync();
        long size = await store.GetSizeAsync();

        Assert.DoesNotContain(entries, static entry => entry.Name == "expired");
        Assert.Contains(entries, static entry => entry.Name == "retained-23");
        Assert.True(entries.Count < 24);
        Assert.InRange(size, 1, maxBytes);
    }

    [Fact]
    public void LocalDiagnosticsStore_RetentionSelectionIsSinglePassAndBoundedByNewestRows()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-07-27T12:00:00Z");
        string expired = SerializeDiagnostic("expired", now.Subtract(TimeSpan.FromDays(15)));
        string first = SerializeDiagnostic("first", now);
        string second = SerializeDiagnostic("second", now);
        string third = SerializeDiagnostic("third", now);
        string oversized = JsonSerializer.Serialize(
            new LocalDiagnosticEvent(
                now,
                "event",
                "oversized",
                new Dictionary<string, string> { ["result"] = new string('x', 4096) }),
            LocalDiagnosticsJsonContext.Default.DiagnosticEvent);
        long maxBytes = Encoding.UTF8.GetByteCount(second + Environment.NewLine + third + Environment.NewLine);
        SinglePassEnumerable<string> source = new([
            expired,
            first,
            "{not-json}",
            second,
            oversized,
            third
        ]);

        IReadOnlyList<string> retained = LocalDiagnosticsStore.SelectRetainedLines(
            source,
            now.Subtract(TimeSpan.FromDays(14)),
            maxBytes);

        Assert.Equal(1, source.EnumerationCount);
        Assert.Equal([second, third], retained);
    }

    [Fact]
    public async Task LocalDiagnosticsStore_EnforcesAgeRetentionDuringLongRunningSession()
    {
        string path = Path.Combine(_root, "diagnostics-live-retention.ndjson");
        await using LocalDiagnosticsStore store = new(
            path,
            maxBytes: 1024 * 1024,
            retention: TimeSpan.FromMilliseconds(40));

        await store.AppendAsync(new LocalDiagnosticEvent(
            DateTimeOffset.UtcNow,
            "event",
            "diagnostics.test",
            new Dictionary<string, string>()));
        await Task.Delay(80);

        Assert.Empty(await store.ReadAsync());
        Assert.Equal(0, await store.GetSizeAsync());
    }

    [Fact]
    public async Task LocalDiagnosticsStore_ContinuesAfterOneFileOperationFails()
    {
        string path = Path.Combine(_root, "diagnostics-fault.ndjson");
        await using LocalDiagnosticsStore store = new(path, 1024 * 1024, TimeSpan.FromDays(14));
        Directory.CreateDirectory(path);

        await Assert.ThrowsAnyAsync<Exception>(
            () => store.AppendAsync(CreateDiagnostic("expected-failure")));

        Directory.Delete(path);
        await store.AppendAsync(CreateDiagnostic("recovered"));
        IReadOnlyList<LocalDiagnosticEvent> entries = await store.ReadAsync();

        LocalDiagnosticEvent recovered = Assert.Single(entries);
        Assert.Equal("recovered", recovered.Name);
    }

    [Fact]
    public async Task TelemetryService_RespectsDiagnosticsEnabledSetting()
    {
        string path = Path.Combine(_root, "diagnostics-disabled.ndjson");
        await using LocalDiagnosticsStore store = new(path, maxBytes: 1024 * 1024, TimeSpan.FromDays(14));
        MemorySettingService settings = new();
        settings.Save(SettingsKeys.DiagnosticsEnabled, false);
        CountingStoreTelemetrySink sink = new(isAvailable: true);
        TelemetryService telemetry = new(store, sink, settings);

        telemetry.TrackEvent("shell.search.submitted");
        await store.FlushAsync();

        Assert.Empty(await store.ReadAsync());
        Assert.Equal(1, sink.Count);
    }

    [Fact]
    public async Task TelemetryService_RespectsStoreTelemetryEnabledSetting()
    {
        string path = Path.Combine(_root, "store-disabled.ndjson");
        await using LocalDiagnosticsStore store = new(path, maxBytes: 1024 * 1024, TimeSpan.FromDays(14));
        MemorySettingService settings = new();
        settings.Save(SettingsKeys.StoreTelemetryEnabled, false);
        CountingStoreTelemetrySink sink = new(isAvailable: true);
        TelemetryService telemetry = new(store, sink, settings);

        telemetry.TrackEvent("shell.search.submitted");
        await store.FlushAsync();

        Assert.Single(await store.ReadAsync());
        Assert.Equal(0, sink.Count);
    }

    [Fact]
    public void TelemetryService_UsesOnlyTheBoundedNonBlockingDiagnosticsPath()
    {
        NonBlockingDiagnosticsStore store = new();
        MemorySettingService settings = new();
        TelemetryService telemetry = new(store, new CountingStoreTelemetrySink(isAvailable: false), settings);

        telemetry.TrackEvent("shell.search.submitted");

        Assert.Equal(1, store.TryAppendCount);
        Assert.Equal(0, store.AppendAsyncCount);
    }

    [Fact]
    public void TelemetryService_AllPublicOperationsAreNoThrowWhenDependenciesFault()
    {
        TelemetryService storeFault = new(
            new ThrowingDiagnosticsStore(),
            new CountingStoreTelemetrySink(isAvailable: false),
            new MemorySettingService());
        TelemetryService sinkFault = new(
            new NonBlockingDiagnosticsStore(),
            new ThrowingStoreTelemetrySink(),
            new MemorySettingService());
        TelemetryService settingsFault = new(
            new NonBlockingDiagnosticsStore(),
            new CountingStoreTelemetrySink(isAvailable: true),
            new ThrowingSettingService());

        Exception? exception = Record.Exception(() =>
        {
            storeFault.TrackEvent("settings.opened");
            sinkFault.TrackMetric("prefetch.policy.decision", 1);
            settingsFault.TrackEvent("auth.opened");
            using IPerformanceTrace trace = settingsFault.StartTrace("profile.loaded");
            trace.SetProperty("result", "success");
        });

        Assert.Null(exception);
    }

    private static LocalDiagnosticEvent CreateDiagnostic(
        string name,
        DateTimeOffset? timestamp = null) =>
        new(
            timestamp ?? DateTimeOffset.UtcNow,
            "event",
            name,
            new Dictionary<string, string> { ["result"] = "success" });

    private static string SerializeDiagnostic(string name, DateTimeOffset timestamp) =>
        JsonSerializer.Serialize(
            CreateDiagnostic(name, timestamp),
            LocalDiagnosticsJsonContext.Default.DiagnosticEvent);

    private sealed class SinglePassEnumerable<T> : IEnumerable<T>
    {
        private readonly IReadOnlyList<T> _items;

        public SinglePassEnumerable(IReadOnlyList<T> items)
        {
            _items = items;
        }

        public int EnumerationCount { get; private set; }

        public IEnumerator<T> GetEnumerator()
        {
            EnumerationCount++;
            if (EnumerationCount > 1)
            {
                throw new InvalidOperationException("The retention source was enumerated more than once.");
            }

            return _items.GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class NonBlockingDiagnosticsStore : ILocalDiagnosticsStore
    {
        public int TryAppendCount { get; private set; }

        public int AppendAsyncCount { get; private set; }

        public bool TryAppend(LocalDiagnosticEvent entry)
        {
            TryAppendCount++;
            return true;
        }

        public Task AppendAsync(LocalDiagnosticEvent entry, CancellationToken cancellationToken = default)
        {
            AppendAsyncCount++;
            throw new InvalidOperationException("Telemetry must not schedule an asynchronous fallback write.");
        }

        public Task<IReadOnlyList<LocalDiagnosticEvent>> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LocalDiagnosticEvent>>(Array.Empty<LocalDiagnosticEvent>());

        public Task ClearAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<long> GetSizeAsync(CancellationToken cancellationToken = default) => Task.FromResult(0L);

        public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CountingStoreTelemetrySink : IStoreTelemetrySink
    {
        public CountingStoreTelemetrySink(bool isAvailable)
        {
            IsAvailable = isAvailable;
            AvailabilityStatus = isAvailable ? "available" : "store_engagement_type_unavailable";
        }

        public bool IsAvailable { get; }

        public string AvailabilityStatus { get; }

        public int Count { get; private set; }

        public List<string> Events { get; } = [];

        public void TrackEvent(string name)
        {
            Count++;
            Events.Add(name);
        }
    }

    private sealed class ThrowingStoreTelemetrySink : IStoreTelemetrySink
    {
        public bool IsAvailable => throw new InvalidOperationException("availability failure");

        public string AvailabilityStatus => "fault";

        public void TrackEvent(string name) => throw new InvalidOperationException("sink failure");
    }

    private sealed class ThrowingTelemetryService : ITelemetryService
    {
        public void TrackEvent(string name, IReadOnlyDictionary<string, string?>? properties = null) =>
            throw new InvalidOperationException("event failure");

        public void TrackMetric(string name, double value, IReadOnlyDictionary<string, string?>? properties = null) =>
            throw new InvalidOperationException("metric failure");

        public IPerformanceTrace StartTrace(string name, IReadOnlyDictionary<string, string?>? properties = null) =>
            new ThrowingPerformanceTrace();

        private sealed class ThrowingPerformanceTrace : IPerformanceTrace
        {
            public void SetProperty(string key, string? value) =>
                throw new InvalidOperationException("trace property failure");

            public void Dispose() => throw new InvalidOperationException("trace dispose failure");
        }
    }

    private sealed class ThrowingSettingService : ISettingService
    {
        public bool Contains(string key) => throw new InvalidOperationException("settings failure");

        public void Save<T>(string key, T value) => throw new InvalidOperationException("settings failure");

        public T Get<T>(string key) => throw new InvalidOperationException("settings failure");
    }

    private sealed class ThrowingDiagnosticsStore : ILocalDiagnosticsStore
    {
        public bool TryAppend(LocalDiagnosticEvent entry) => throw new IOException("diagnostics failure");

        public Task AppendAsync(LocalDiagnosticEvent entry, CancellationToken cancellationToken = default) =>
            Task.FromException(new IOException("diagnostics failure"));

        public Task<IReadOnlyList<LocalDiagnosticEvent>> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromException<IReadOnlyList<LocalDiagnosticEvent>>(new IOException("diagnostics failure"));

        public Task ClearAsync(CancellationToken cancellationToken = default) =>
            Task.FromException(new IOException("diagnostics failure"));

        public Task<long> GetSizeAsync(CancellationToken cancellationToken = default) =>
            Task.FromException<long>(new IOException("diagnostics failure"));

        public Task FlushAsync(CancellationToken cancellationToken = default) =>
            Task.FromException(new IOException("diagnostics failure"));

        public Task ShutdownAsync(CancellationToken cancellationToken = default) =>
            Task.FromException(new IOException("diagnostics failure"));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

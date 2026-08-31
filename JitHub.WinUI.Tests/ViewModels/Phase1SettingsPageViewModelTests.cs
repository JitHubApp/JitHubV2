using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models;
using JitHub.Services;
using JitHub.WinUI.Tests.Services;
using JitHub.WinUI.Tests.TestDoubles;
using JitHub.WinUI.ViewModels.Pages;
using Xunit;

namespace JitHub.WinUI.Tests.ViewModels;

public sealed class Phase1SettingsPageViewModelTests
{
    [Fact]
    public async Task Initialize_LoadsThemeVersionDeveloperModeTogglesAndSnapshot()
    {
        FakePreferencesService preferences = new()
        {
            Theme = ThemeConst.Dark,
            Palette = ThemePaletteIds.GitHub,
            IsDeveloperMode = true,
            VersionText = "1.2.3.4"
        };
        MemorySettingService settings = new();
        FakeSettingsDiagnosticsService diagnostics = new(CreateSnapshot(metadataBytes: 128, payloadBytes: 256, imageBytes: 64, diagnosticsBytes: 32));
        RecordingTelemetryService telemetry = new();
        SettingsPageViewModel viewModel = new(preferences, settings, diagnostics, telemetry);

        await viewModel.InitializeAsync();

        Assert.Equal("JitHub 1.2.3.4", viewModel.VersionText);
        Assert.True(viewModel.IsDeveloperMode);
        Assert.Equal(ThemeConst.Dark, viewModel.SelectedThemeOption?.Value);
        Assert.Equal(ThemePaletteIds.GitHub, viewModel.SelectedPaletteOption?.Id);
        Assert.True(viewModel.DiagnosticsEnabled);
        Assert.True(viewModel.StoreTelemetryEnabled);
        Assert.True(viewModel.CanUseStoreTelemetry);
        Assert.Equal("128 B", viewModel.MetadataSizeText);
        Assert.Equal("Settings snapshot is current.", viewModel.StatusText);
        Assert.Contains(telemetry.Events, static entry => entry.Name == "settings.opened");
        RecordedTelemetryEvent loaded = Assert.Single(telemetry.Events, static entry => entry.Name == "settings.loaded");
        Assert.Equal("success", loaded.Properties["result"]);
        Assert.False(string.IsNullOrWhiteSpace(loaded.Properties["duration_bucket"]));
    }

    [Fact]
    public async Task ChangingThemeAndToggles_PersistsFutureSettings()
    {
        FakePreferencesService preferences = new() { Theme = ThemeConst.System };
        MemorySettingService settings = new();
        FakeSettingsDiagnosticsService diagnostics = new(CreateSnapshot());
        RecordingTelemetryService telemetry = new();
        SettingsPageViewModel viewModel = new(preferences, settings, diagnostics, telemetry);
        await viewModel.InitializeAsync();

        viewModel.SelectedThemeOption = viewModel.ThemeOptions.Single(option => option.Value == ThemeConst.Light);
        viewModel.SelectedPaletteOption = viewModel.PaletteOptions.Single(option => option.Id == ThemePaletteIds.Solarized);
        viewModel.DiagnosticsEnabled = false;
        viewModel.StoreTelemetryEnabled = false;
        viewModel.IsDeveloperMode = true;

        Assert.Equal(ThemeConst.Light, preferences.Theme);
        Assert.Equal(ThemePaletteIds.Solarized, preferences.Palette);
        Assert.False(settings.Get<bool>(SettingsKeys.DiagnosticsEnabled));
        Assert.False(settings.Get<bool>(SettingsKeys.StoreTelemetryEnabled));
        Assert.True(preferences.IsDeveloperMode);
        Assert.Contains(telemetry.Events, static entry =>
            entry.Name == "settings.action.executed" && entry.Properties["action"] == "theme_changed");
        Assert.Contains(telemetry.Events, static entry =>
            entry.Name == "settings.action.executed" &&
            entry.Properties["action"] == "theme_palette_changed" &&
            entry.Properties["theme_palette"] == ThemePaletteIds.Solarized);
    }

    [Fact]
    public async Task PaletteChangeFailure_RestoresPreviousSelectionAndReportsError()
    {
        FakePreferencesService preferences = new()
        {
            Palette = ThemePaletteIds.GitHub,
            PaletteApplySucceeds = false
        };
        RecordingTelemetryService telemetry = new();
        SettingsPageViewModel viewModel = new(
            preferences,
            new MemorySettingService(),
            new FakeSettingsDiagnosticsService(CreateSnapshot()),
            telemetry);
        await viewModel.InitializeAsync();

        viewModel.SelectedPaletteOption = viewModel.PaletteOptions.Single(
            option => option.Id == ThemePaletteIds.VisualStudioCode);

        Assert.Equal(ThemePaletteIds.GitHub, preferences.Palette);
        Assert.Equal(ThemePaletteIds.GitHub, viewModel.SelectedPaletteOption?.Id);
        Assert.True(viewModel.HasStatusError);
        Assert.Equal(
            "JitHub could not apply that color theme. Your previous theme is still active.",
            viewModel.StatusText);
        Assert.True(viewModel.PaletteOptions.Single(option => option.Id == ThemePaletteIds.GitHub).IsSelected);
        Assert.False(viewModel.PaletteOptions.Single(option => option.Id == ThemePaletteIds.VisualStudioCode).IsSelected);
        RecordedTelemetryEvent action = Assert.Single(telemetry.Events, static entry =>
            entry.Name == "settings.action.executed" &&
            entry.Properties["action"] == TelemetryTaxonomy.Actions.ThemePaletteChanged);
        Assert.Equal(ThemePaletteIds.VisualStudioCode, action.Properties["theme_palette"]);
        Assert.Equal(TelemetryTaxonomy.Results.Error, action.Properties["result"]);
    }

    [Fact]
    public async Task ClearCommand_UpdatesBusyStateStatusAndRefreshedSizes()
    {
        FakeSettingsDiagnosticsService diagnostics = new(CreateSnapshot(payloadBytes: 1024));
        TaskCompletionSource completion = new();
        diagnostics.ClearQueryCacheTask = completion.Task;
        diagnostics.NextSnapshotAfterClear = CreateSnapshot(payloadBytes: 0);
        RecordingTelemetryService telemetry = new();
        SettingsPageViewModel viewModel = new(new FakePreferencesService(), new MemorySettingService(), diagnostics, telemetry);
        await viewModel.InitializeAsync();

        Task clearTask = viewModel.ClearQueryCacheAsync();

        Assert.True(viewModel.IsBusy);
        Assert.Equal("Clearing GitHub query cache...", viewModel.StatusText);

        completion.SetResult();
        await clearTask;

        Assert.False(viewModel.IsBusy);
        Assert.Equal("0 B", viewModel.PayloadSizeText);
        Assert.Equal("GitHub query cache was cleared.", viewModel.StatusText);
        RecordedTelemetryEvent action = Assert.Single(telemetry.Events, static entry =>
            entry.Name == "settings.action.executed" && entry.Properties["action"] == "clear_query_cache");
        Assert.Equal("success", action.Properties["result"]);
    }

    [Fact]
    public async Task FailedClear_ShowsErrorAndKeepsExistingSnapshot()
    {
        FakeSettingsDiagnosticsService diagnostics = new(CreateSnapshot(metadataBytes: 128, payloadBytes: 512));
        diagnostics.ThrowOnClearQueryCache = true;
        RecordingTelemetryService telemetry = new();
        SettingsPageViewModel viewModel = new(new FakePreferencesService(), new MemorySettingService(), diagnostics, telemetry);
        await viewModel.InitializeAsync();

        await viewModel.ClearQueryCacheAsync();

        Assert.Equal("JitHub could not complete this action. Try again.", viewModel.StatusText);
        Assert.Equal("128 B", viewModel.MetadataSizeText);
        Assert.Equal("512 B", viewModel.PayloadSizeText);
        RecordedTelemetryEvent error = Assert.Single(telemetry.Events, static entry =>
            entry.Name == "settings.error" && entry.Properties["action"] == "clear_query_cache");
        Assert.Equal("unexpected", error.Properties["error_kind"]);
    }

    [Fact]
    public async Task ExportDiagnostics_UpdatesStatusWithoutReloadingSnapshot()
    {
        FakeSettingsDiagnosticsService diagnostics = new(CreateSnapshot(diagnosticsBytes: 42));
        SettingsPageViewModel viewModel = new(
            new FakePreferencesService(),
            new MemorySettingService(),
            diagnostics,
            new RecordingTelemetryService());
        await viewModel.InitializeAsync();

        await viewModel.ExportDiagnosticsAsync(Path.Combine(Path.GetTempPath(), "jithub-export.ndjson"));

        Assert.Equal(1, diagnostics.ExportCount);
        Assert.Equal("42 B", viewModel.DiagnosticsSizeText);
        Assert.Equal("Diagnostics were exported.", viewModel.StatusText);
    }

    [Fact]
    public async Task SwitchingSections_PreservesLoadedSnapshot()
    {
        FakeSettingsDiagnosticsService diagnostics = new(CreateSnapshot(metadataBytes: 99));
        SettingsPageViewModel viewModel = new(
            new FakePreferencesService(),
            new MemorySettingService(),
            diagnostics,
            new RecordingTelemetryService());
        await viewModel.InitializeAsync();
        SettingsDiagnosticsSnapshot? original = viewModel.Snapshot;

        viewModel.SelectedSection = viewModel.SettingsSections.Single(section => section.Title == "Diagnostics");

        Assert.True(viewModel.IsDiagnosticsSelected);
        Assert.Same(original, viewModel.Snapshot);
        Assert.Equal("99 B", viewModel.MetadataSizeText);
    }

    [Fact]
    public void SettingsSectionItem_UsesItsLocalizedTitleAsTheDisplayString()
    {
        SettingsSectionItem section = new("appearance", "Appearance", "glyph");

        Assert.Equal("Appearance", section.ToString());
    }

    [Fact]
    public async Task DiagnosticsToggle_EmitsOutcomeOnlyAfterPersistence()
    {
        List<string> order = [];
        RecordingSettingService settings = new(order);
        OrderedTelemetryService telemetry = new(order);
        SettingsPageViewModel viewModel = new(
            new FakePreferencesService(),
            settings,
            new FakeSettingsDiagnosticsService(CreateSnapshot()),
            telemetry);
        await viewModel.InitializeAsync();
        order.Clear();

        viewModel.DiagnosticsEnabled = false;

        Assert.Equal(["save:DiagnosticsEnabled", "telemetry:disabled"], order);
    }

    [Fact]
    public async Task DiagnosticsToggle_PersistenceFailureRevertsAndReportsErrorOnly()
    {
        ThrowingSettingService settings = new();
        RecordingTelemetryService telemetry = new();
        SettingsPageViewModel viewModel = new(
            new FakePreferencesService(),
            settings,
            new FakeSettingsDiagnosticsService(CreateSnapshot()),
            telemetry);
        await viewModel.InitializeAsync();

        viewModel.DiagnosticsEnabled = false;

        Assert.True(viewModel.DiagnosticsEnabled);
        RecordedTelemetryEvent action = Assert.Single(
            telemetry.Events,
            static entry => entry.Name == "settings.action.executed"
                && entry.Properties["action"] == TelemetryTaxonomy.Actions.Diagnostics);
        Assert.Equal(TelemetryTaxonomy.Results.Error, action.Properties["result"]);
        Assert.DoesNotContain(
            telemetry.Events,
            static entry => entry.Name == "settings.action.executed"
                && entry.Properties["result"] == TelemetryTaxonomy.Results.Disabled);
    }

    [Fact]
    public async Task Initialize_ProjectsCacheOwnerHealthAndPolicyWithoutDroppingDegradedOwner()
    {
        CacheOwnerSnapshot owner = new(
            CacheOwnerIds.GitHubQuery,
            "GitHub query cache",
            ["cache.db", "payloads"],
            Bytes: 4096,
            SoftCapBytes: null,
            TtlPolicy: "5 minutes mutable; 30 minutes repository metadata",
            AccountPartition: "Authenticated GitHub user ID",
            ClearSemantics: "Cleared with GitHub query cache",
            IsDurableUserData: false,
            CacheOwnerHealth.Degraded,
            "One orphan generation.",
            [new CacheOwnerCap("Payload content", 2048)],
            LogicalBytes: 1024,
            OrphanBytes: 128);
        SettingsPageViewModel viewModel = new(
            new FakePreferencesService(),
            new MemorySettingService(),
            new FakeSettingsDiagnosticsService(CreateSnapshot(cacheOwners: [owner])),
            new RecordingTelemetryService());

        await viewModel.InitializeAsync();

        SettingsCacheOwnerItem projected = Assert.Single(viewModel.CacheOwners);
        Assert.Equal("Degraded", projected.Health);
        Assert.Contains("4.0 KB physical", projected.Size, StringComparison.Ordinal);
        Assert.Contains("128 B orphaned", projected.Size, StringComparison.Ordinal);
        Assert.Contains("Payload content 2.0 KB", projected.Policy, StringComparison.Ordinal);
        Assert.Equal("One orphan generation.", projected.HealthDetail);
    }

    private static SettingsDiagnosticsSnapshot CreateSnapshot(
        long metadataBytes = 0,
        long payloadBytes = 0,
        long imageBytes = 0,
        long diagnosticsBytes = 0,
        bool storeAvailable = true,
        bool storeEnabled = true,
        bool diagnosticsEnabled = true,
        IReadOnlyList<CacheOwnerSnapshot>? cacheOwners = null) =>
        new(
            new CacheStorageSummary(
                "cache.db",
                "payloads",
                "images",
                metadataBytes,
                payloadBytes,
                imageBytes,
                1),
            new DiagnosticsStorageSummary("diagnostics.ndjson", diagnosticsBytes),
            new StoreTelemetrySummary(
                storeAvailable ? "Available" : "Disabled by compatibility",
                storeAvailable,
                storeAvailable && storeEnabled,
                !storeAvailable),
            diagnosticsEnabled,
            storeAvailable && storeEnabled,
            CacheOwners: cacheOwners);

    private sealed class FakePreferencesService : ISettingsPreferencesService
    {
        public string Theme { get; set; } = ThemeConst.System;

        public string Palette { get; set; } = ThemePaletteIds.JitHub;

        public bool IsDeveloperMode { get; set; }

        public string VersionText { get; set; } = "0.0.0.0";

        public bool PaletteApplySucceeds { get; set; } = true;

        public string GetTheme() => Theme;

        public void SetTheme(string theme) => Theme = theme;

        public string GetPalette() => Palette;

        public bool TrySetPalette(string paletteId)
        {
            if (!PaletteApplySucceeds)
            {
                return false;
            }

            Palette = ThemePaletteCatalog.Normalize(paletteId);
            return true;
        }

        public string GetVersionText() => VersionText;
    }

    private sealed class RecordingSettingService(List<string> order) : ISettingService
    {
        private readonly Dictionary<string, object?> _values = [];

        public bool Contains(string key) => _values.ContainsKey(key);

        public void Save<T>(string key, T value)
        {
            order.Add($"save:{key}");
            _values[key] = value;
        }

        public T Get<T>(string key) =>
            _values.TryGetValue(key, out object? value) && value is T typed ? typed : default!;
    }

    private sealed class ThrowingSettingService : ISettingService
    {
        public bool Contains(string key) => false;

        public void Save<T>(string key, T value) => throw new InvalidOperationException("storage unavailable");

        public T Get<T>(string key) => default!;
    }

    private sealed class OrderedTelemetryService(List<string> order) : ITelemetryService
    {
        public void TrackEvent(string name, IReadOnlyDictionary<string, string?>? properties = null)
        {
            if (name == "settings.action.executed")
            {
                order.Add($"telemetry:{properties?["result"]}");
            }
        }

        public void TrackMetric(
            string name,
            double value,
            IReadOnlyDictionary<string, string?>? properties = null)
        {
        }

        public IPerformanceTrace StartTrace(
            string name,
            IReadOnlyDictionary<string, string?>? properties = null) => NoopTrace.Instance;

        private sealed class NoopTrace : IPerformanceTrace
        {
            public static NoopTrace Instance { get; } = new();

            public void SetProperty(string key, string? value)
            {
            }

            public void Dispose()
            {
            }
        }
    }

    private sealed class FakeSettingsDiagnosticsService : ISettingsDiagnosticsService
    {
        private SettingsDiagnosticsSnapshot _snapshot;

        public FakeSettingsDiagnosticsService(SettingsDiagnosticsSnapshot snapshot)
        {
            _snapshot = snapshot;
        }

        public Task ClearQueryCacheTask { get; set; } = Task.CompletedTask;

        public SettingsDiagnosticsSnapshot? NextSnapshotAfterClear { get; set; }

        public bool ThrowOnClearQueryCache { get; set; }

        public int ExportCount { get; private set; }

        public Task<SettingsDiagnosticsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_snapshot);

        public Task ClearDiagnosticsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task ClearQueryCacheAsync(CancellationToken cancellationToken = default)
        {
            if (ThrowOnClearQueryCache)
            {
                throw new InvalidOperationException("boom");
            }

            await ClearQueryCacheTask;
            if (NextSnapshotAfterClear is not null)
            {
                _snapshot = NextSnapshotAfterClear;
            }
        }

        public Task ClearImageCacheAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ClearRepoFileCacheAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ClearStarLibraryAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ClearAllCacheAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ExportDiagnosticsAsync(string destinationPath, CancellationToken cancellationToken = default)
        {
            ExportCount++;
            return Task.CompletedTask;
        }
    }
}

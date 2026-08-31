using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using JitHub.Models;
using JitHub.Services;
using JitHub.WinUI.Helpers;

namespace JitHub.WinUI.ViewModels.Pages;

public sealed partial class SettingsPageViewModel : ObservableObject
{
    private const string AppearanceSectionId = "appearance";
    private const string GeneralSectionId = "general";
    private const string PrivacySectionId = "privacy";
    private const string DataCacheSectionId = "data-cache";
    private const string DiagnosticsSectionId = "diagnostics";
    private const string AboutSectionId = "about";

    private readonly ISettingsPreferencesService _preferencesService;
    private readonly ISettingService _settingService;
    private readonly ISettingsDiagnosticsService _diagnosticsService;
    private readonly ISettingsSourceNavigationService _sourceNavigationService;
    private readonly ITelemetryService _telemetryService;
    private bool _isInitializing;

    private SettingsSectionItem _selectedSection;
    private ThemeOption? _selectedThemeOption;
    private ThemePaletteOption? _selectedPaletteOption;
    private bool _isDeveloperMode;
    private bool _diagnosticsEnabled = true;
    private bool _storeTelemetryEnabled = true;
    private bool _canUseStoreTelemetry;
    private bool _isBusy;
    private bool _hasStatusError;
    private string _versionText = string.Empty;
    private string _statusText = L("Settings/Status/Ready", "Settings are ready.");
    private SettingsDiagnosticsSnapshot? _snapshot;
    private IReadOnlyList<SettingsCacheOwnerItem> _cacheOwners = [];

    public SettingsPageViewModel(
        ISettingsPreferencesService preferencesService,
        ISettingService settingService,
        ISettingsDiagnosticsService diagnosticsService,
        ITelemetryService telemetryService)
        : this(
            preferencesService,
            settingService,
            diagnosticsService,
            telemetryService,
            UnavailableSettingsSourceNavigationService.Instance)
    {
    }

    public SettingsPageViewModel(
        ISettingsPreferencesService preferencesService,
        ISettingService settingService,
        ISettingsDiagnosticsService diagnosticsService,
        ITelemetryService telemetryService,
        ISettingsSourceNavigationService sourceNavigationService)
    {
        _preferencesService = preferencesService;
        _settingService = settingService;
        _diagnosticsService = diagnosticsService;
        _sourceNavigationService = sourceNavigationService;
        _telemetryService = SafeTelemetryService.Wrap(telemetryService);

        bool usePseudoLongLabels = string.Equals(
            Environment.GetEnvironmentVariable("JITHUB_PREVIEW_SCENARIO"),
            "settings-pseudo-long-labels",
            StringComparison.OrdinalIgnoreCase);
        string Label(string standard) => usePseudoLongLabels
            ? string.Join(" ", Enumerable.Repeat(standard, 4))
            : standard;

        SettingsSections = new SettingsSectionItem[]
        {
            new(AppearanceSectionId, Label(L("Settings/Sections/Appearance", "Appearance")), "\uE790"),
            new(GeneralSectionId, Label(L("Settings/Sections/General", "General")), "\uE7BE"),
            new(PrivacySectionId, Label(L("Settings/Sections/Privacy", "Privacy")), "\uE72E"),
            new(DataCacheSectionId, Label(L("Settings/Sections/DataCache", "Data & Cache")), "\uE8B7"),
            new(DiagnosticsSectionId, Label(L("Settings/Sections/Diagnostics", "Diagnostics")), "\uE9D9"),
            new(AboutSectionId, Label(L("Settings/Sections/About", "About")), "\uE946")
        };

        ThemeOptions =
        (ThemeOption[])
        [
            new(ThemeConst.System, L("Settings/Theme/System", "System"), L("Settings/Theme/SystemDescription", "Follow Windows theme")),
            new(ThemeConst.Light, L("Settings/Theme/Light", "Light"), L("Settings/Theme/LightDescription", "Always use light theme")),
            new(ThemeConst.Dark, L("Settings/Theme/Dark", "Dark"), L("Settings/Theme/DarkDescription", "Always use dark theme"))
        ];

        PaletteOptions = CreatePaletteOptions();

        Credits =
        (SettingsCredit[])
        [
            new("Nero Cui", L("Settings/Credits/DeveloperRole", "Developer"), L("Settings/Credits/NeroDescription", "Core app, migration, and product direction.")),
            new("Get", L("Settings/Credits/DeveloperRole", "Developer"), L("Settings/Credits/GetDescription", "Prototype work, app polish, and productivity workflows.")),
            new("Ze Chen", L("Settings/Credits/DeveloperRole", "Developer"), L("Settings/Credits/ZeDescription", "Infrastructure and feature implementation.")),
            new("Xueyang Song", L("Settings/Credits/ResearcherRole", "ML + Battery Researcher"), L("Settings/Credits/XueyangDescription", "Research perspective and applied systems thinking.")),
            new("Keira Xu", L("Settings/Credits/LogoDesignerRole", "Logo Designer"), L("Settings/Credits/KeiraDescription", "Brand, logo, and visual identity.")),
            new("Jakub Bugajski", L("Settings/Credits/UiDesignerRole", "UI Designer"), L("Settings/Credits/JakubDescription", "Interface design direction and polish."))
        ];

        _selectedSection = SettingsSections[0];
    }

    public IReadOnlyList<SettingsSectionItem> SettingsSections { get; }

    public IReadOnlyList<ThemeOption> ThemeOptions { get; }

    public IReadOnlyList<ThemePaletteOption> PaletteOptions { get; }

    public IReadOnlyList<SettingsCredit> Credits { get; }

    public string SettingsTitle => L("Settings/Title", "Settings");

    public async Task OpenSourceRepositoryAsync(CancellationToken cancellationToken = default)
    {
        SettingsSourceNavigationOutcome outcome =
            await _sourceNavigationService.OpenAsync(cancellationToken);
        HasStatusError = outcome.Result is
            SettingsSourceNavigationResult.Unavailable or
            SettingsSourceNavigationResult.Empty or
            SettingsSourceNavigationResult.Error;
        StatusText = outcome.Result switch
        {
            SettingsSourceNavigationResult.Success =>
                L("Settings/Status/SourceOpened", "Opened the JitHub source repository."),
            SettingsSourceNavigationResult.Unavailable =>
                L("Settings/Status/SourceUnavailable", "Sign in to open the JitHub source repository."),
            SettingsSourceNavigationResult.Empty =>
                L("Settings/Status/SourceNotFound", "The JitHub source repository is temporarily unavailable."),
            _ => L("Settings/Status/SourceOpenFailed", "The JitHub source repository could not be opened.")
        };
    }

    public string SectionPickerAutomationName =>
        L("Settings/SectionPickerAutomationName", "Settings section");

    public SettingsSectionItem SelectedSection
    {
        get => _selectedSection;
        set
        {
            if (value is null || !SetProperty(ref _selectedSection, value))
            {
                return;
            }

            NotifySelectedSectionChanged();
            if (!_isInitializing)
            {
                TrackEvent("settings.action.executed", new Dictionary<string, string?>
                {
                    ["action"] = TelemetryTaxonomy.Actions.SectionChanged,
                    ["section"] = value.Id,
                    ["result"] = TelemetryTaxonomy.Results.Success
                });
            }
        }
    }

    public ThemeOption? SelectedThemeOption
    {
        get => _selectedThemeOption;
        set
        {
            if (!SetProperty(ref _selectedThemeOption, value))
            {
                return;
            }

            NotifyThemeSelectionChanged();

            if (value is null || _isInitializing)
            {
                return;
            }

            _preferencesService.SetTheme(NormalizeTheme(value.Value));
            StatusText = LF("Settings/Status/ThemeChangedFormat", "Theme changed to {0}.", value.Label);
            TrackEvent("settings.action.executed", new Dictionary<string, string?>
            {
                ["action"] = TelemetryTaxonomy.Actions.ThemeChanged,
                ["view_mode"] = NormalizeTheme(value.Value).ToLowerInvariant(),
                ["result"] = TelemetryTaxonomy.Results.Success
            });
        }
    }

    public bool IsSystemThemeSelected => IsThemeSelected(ThemeConst.System);

    public bool IsLightThemeSelected => IsThemeSelected(ThemeConst.Light);

    public bool IsDarkThemeSelected => IsThemeSelected(ThemeConst.Dark);

    public ThemePaletteOption? SelectedPaletteOption
    {
        get => _selectedPaletteOption;
        set
        {
            if (value is null || ReferenceEquals(_selectedPaletteOption, value))
            {
                return;
            }

            ThemePaletteOption? previous = _selectedPaletteOption;
            _selectedPaletteOption = value;
            OnPropertyChanged();
            UpdatePaletteSelection();

            if (_isInitializing)
            {
                return;
            }

            bool applied;
            try
            {
                applied = _preferencesService.TrySetPalette(value.Id);
            }
            catch (Exception)
            {
                applied = false;
            }

            if (!applied)
            {
                _selectedPaletteOption = previous;
                OnPropertyChanged();
                UpdatePaletteSelection();
                HasStatusError = true;
                StatusText = L(
                    "Settings/Status/PaletteChangeFailed",
                    "JitHub could not apply that color theme. Your previous theme is still active.");
                TrackEvent("settings.action.executed", new Dictionary<string, string?>
                {
                    ["action"] = TelemetryTaxonomy.Actions.ThemePaletteChanged,
                    ["theme_palette"] = value.Id,
                    ["result"] = TelemetryTaxonomy.Results.Error
                });
                return;
            }

            HasStatusError = false;
            StatusText = LF(
                "Settings/Status/PaletteChangedFormat",
                "Color theme changed to {0}.",
                value.Label);
            TrackEvent("settings.action.executed", new Dictionary<string, string?>
            {
                ["action"] = TelemetryTaxonomy.Actions.ThemePaletteChanged,
                ["theme_palette"] = value.Id,
                ["result"] = TelemetryTaxonomy.Results.Success
            });
        }
    }

    public bool CanResetPalette =>
        !string.Equals(SelectedPaletteOption?.Id, ThemePaletteIds.JitHub, StringComparison.Ordinal);

    public bool IsDeveloperMode
    {
        get => _isDeveloperMode;
        set
        {
            if (!SetProperty(ref _isDeveloperMode, value) || _isInitializing)
            {
                return;
            }

            _preferencesService.IsDeveloperMode = value;
            StatusText = value
                ? L("Settings/Status/DeveloperModeOn", "Developer mode is on.")
                : L("Settings/Status/DeveloperModeOff", "Developer mode is off.");
            TrackEvent(
                "settings.action.executed",
                ToggleProperties(TelemetryTaxonomy.Actions.DeveloperMode, value));
        }
    }

    public bool DiagnosticsEnabled
    {
        get => _diagnosticsEnabled;
        set
        {
            bool previousValue = _diagnosticsEnabled;
            if (!SetProperty(ref _diagnosticsEnabled, value) || _isInitializing)
            {
                return;
            }

            try
            {
                _settingService.Save(SettingsKeys.DiagnosticsEnabled, value);
                StatusText = value
                    ? L("Settings/Status/DiagnosticsOn", "Local diagnostics will be collected for future events.")
                    : L("Settings/Status/DiagnosticsOff", "Local diagnostics collection is off for future events.");
                HasStatusError = false;
                TrackEvent(
                    "settings.action.executed",
                    ToggleProperties(TelemetryTaxonomy.Actions.Diagnostics, value));
            }
            catch (Exception ex)
            {
                SetProperty(ref _diagnosticsEnabled, previousValue, nameof(DiagnosticsEnabled));
                ReportSettingPersistenceFailure(TelemetryTaxonomy.Actions.Diagnostics, ex);
            }
        }
    }

    public bool StoreTelemetryEnabled
    {
        get => _storeTelemetryEnabled;
        set
        {
            bool nextValue = CanUseStoreTelemetry && value;
            bool previousValue = _storeTelemetryEnabled;
            if (!SetProperty(ref _storeTelemetryEnabled, nextValue) || _isInitializing)
            {
                return;
            }

            try
            {
                _settingService.Save(SettingsKeys.StoreTelemetryEnabled, nextValue);
                StatusText = nextValue
                    ? L("Settings/Status/StoreTelemetryOn", "Store custom events will be sent for future events.")
                    : L("Settings/Status/StoreTelemetryOff", "Store custom events are off for future events.");
                HasStatusError = false;
                TrackEvent(
                    "settings.action.executed",
                    ToggleProperties(TelemetryTaxonomy.Actions.StoreTelemetry, nextValue));
            }
            catch (Exception ex)
            {
                SetProperty(ref _storeTelemetryEnabled, previousValue, nameof(StoreTelemetryEnabled));
                ReportSettingPersistenceFailure(TelemetryTaxonomy.Actions.StoreTelemetry, ex);
            }
        }
    }

    public bool CanUseStoreTelemetry
    {
        get => _canUseStoreTelemetry;
        private set
        {
            if (SetProperty(ref _canUseStoreTelemetry, value))
            {
                OnPropertyChanged(nameof(StoreTelemetryAvailabilityText));
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public bool HasStatusError
    {
        get => _hasStatusError;
        private set => SetProperty(ref _hasStatusError, value);
    }

    public string VersionText
    {
        get => _versionText;
        private set => SetProperty(ref _versionText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public SettingsDiagnosticsSnapshot? Snapshot
    {
        get => _snapshot;
        private set
        {
            if (SetProperty(ref _snapshot, value))
            {
                CacheOwners = BuildCacheOwnerItems(value?.CacheOwners);
                NotifySnapshotChanged();
            }
        }
    }

    public IReadOnlyList<SettingsCacheOwnerItem> CacheOwners
    {
        get => _cacheOwners;
        private set => SetProperty(ref _cacheOwners, value);
    }

    public bool IsAppearanceSelected => IsSelected(AppearanceSectionId);

    public bool IsGeneralSelected => IsSelected(GeneralSectionId);

    public bool IsPrivacySelected => IsSelected(PrivacySectionId);

    public bool IsDataCacheSelected => IsSelected(DataCacheSectionId);

    public bool IsDiagnosticsSelected => IsSelected(DiagnosticsSectionId);

    public bool IsAboutSelected => IsSelected(AboutSectionId);

    public string StoreTelemetryStatusText => Snapshot?.StoreTelemetry.Status ?? L("Settings/StoreTelemetry/Checking", "Checking");

    public string StoreTelemetryAvailabilityText => CanUseStoreTelemetry
        ? L("Settings/StoreTelemetry/AvailableDescription", "Store custom events are available for privacy-safe feature telemetry.")
        : L("Settings/StoreTelemetry/UnavailableDescription", "Store custom events are unavailable here. Local diagnostics still work.");

    public string CacheDatabasePath => Snapshot?.Cache.DatabasePath ?? string.Empty;

    public string PayloadPath => Snapshot?.Cache.PayloadPath ?? string.Empty;

    public string ImagePath => Snapshot?.Cache.ImagePath ?? string.Empty;

    public string DiagnosticsPath => Snapshot?.Diagnostics.Path ?? string.Empty;

    public string StarLibraryPath => Snapshot?.StarLibrary is { } stars
        ? $"{stars.DatabasePath} | recovery: {stars.RecoveryJournalPath}"
        : string.Empty;

    public string RepoFileCachePath => Snapshot?.RepoFiles?.RootPath ?? string.Empty;

    public string MetadataSizeText => FormatBytes(Snapshot?.Cache.MetadataBytes ?? 0);

    public string PayloadSizeText => FormatBytes(Snapshot?.Cache.PayloadBytes ?? 0);

    public string ImageSizeText => FormatBytes(Snapshot?.Cache.ImageBytes ?? 0);

    public string DiagnosticsSizeText => FormatBytes(Snapshot?.Diagnostics.Bytes ?? 0);

    public string StarLibrarySizeText => FormatBytes(Snapshot?.StarLibrary?.Bytes ?? 0);

    public string RepoFileCacheSizeText => FormatBytes(Snapshot?.RepoFiles?.Bytes ?? 0);

    public string TotalCacheSizeText => FormatBytes(Snapshot?.Cache.TotalBytes ?? 0);

    public string SchemaVersionText => Snapshot is null
        ? L("Common/Unknown", "Unknown")
        : Snapshot.Cache.SchemaVersion > 0
            ? Snapshot.Cache.SchemaVersion.ToString(CultureInfo.InvariantCulture)
            : L("Common/Unknown", "Unknown");

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        TrackEvent("settings.opened", new Dictionary<string, string?>
        {
            ["page"] = "settings",
            ["source"] = TelemetryTaxonomy.Sources.Route
        });
        _isInitializing = true;
        try
        {
            VersionText = LF("Settings/About/VersionFormat", "JitHub {0}", _preferencesService.GetVersionText());
            IsDeveloperMode = _preferencesService.IsDeveloperMode;
            SelectedThemeOption = FindThemeOption(_preferencesService.GetTheme());
            SelectedPaletteOption = FindPaletteOption(_preferencesService.GetPalette());
            await RunRefreshAsync(cancellationToken, "initial");
        }
        catch (Exception ex)
        {
            TrackEvent("settings.error", new Dictionary<string, string?>
            {
                ["page"] = "settings",
                ["source"] = TelemetryTaxonomy.Sources.Initial,
                ["error_kind"] = GetErrorKind(ex),
                ["result"] = TelemetryTaxonomy.Results.Error
            });
            throw;
        }
        finally
        {
            _isInitializing = false;
        }
    }

    public Task RefreshDiagnosticsAsync(CancellationToken cancellationToken = default) =>
        RunRefreshAsync(cancellationToken, TelemetryTaxonomy.Sources.Action);

    public Task ClearDiagnosticsAsync(CancellationToken cancellationToken = default) =>
        RunMutationAsync(
            L("Settings/Status/ClearingDiagnostics", "Clearing diagnostics..."),
            L("Settings/Status/DiagnosticsCleared", "Diagnostics were cleared."),
            TelemetryTaxonomy.Actions.ClearDiagnostics,
            token => _diagnosticsService.ClearDiagnosticsAsync(token),
            cancellationToken);

    public Task ClearQueryCacheAsync(CancellationToken cancellationToken = default) =>
        RunMutationAsync(
            L("Settings/Status/ClearingQueryCache", "Clearing GitHub query cache..."),
            L("Settings/Status/QueryCacheCleared", "GitHub query cache was cleared."),
            TelemetryTaxonomy.Actions.ClearQueryCache,
            token => _diagnosticsService.ClearQueryCacheAsync(token),
            cancellationToken);

    public Task ClearImageCacheAsync(CancellationToken cancellationToken = default) =>
        RunMutationAsync(
            L("Settings/Status/ClearingImageCache", "Clearing avatar and image cache..."),
            L("Settings/Status/ImageCacheCleared", "Avatar and image cache was cleared."),
            TelemetryTaxonomy.Actions.ClearImageCache,
            token => _diagnosticsService.ClearImageCacheAsync(token),
            cancellationToken);

    public Task ClearRepoFileCacheAsync(CancellationToken cancellationToken = default) =>
        RunMutationAsync(
            L("Settings/Status/ClearingRepoFileCache", "Clearing repository file cache..."),
            L("Settings/Status/RepoFileCacheCleared", "Repository file cache was cleared."),
            TelemetryTaxonomy.Actions.ClearRepoFileCache,
            token => _diagnosticsService.ClearRepoFileCacheAsync(token),
            cancellationToken);

    public Task ClearAllCacheAsync(CancellationToken cancellationToken = default) =>
        RunMutationAsync(
            L("Settings/Status/ClearingAllCache", "Clearing Phase 0 cache data..."),
            L("Settings/Status/AllCacheCleared", "Phase 0 cache data was cleared."),
            TelemetryTaxonomy.Actions.ClearAllCache,
            token => _diagnosticsService.ClearAllCacheAsync(token),
            cancellationToken);

    public Task ClearStarLibraryAsync(CancellationToken cancellationToken = default) =>
        RunMutationAsync(
            L("Settings/Status/ClearingStarsLibrary", "Clearing the Stars library..."),
            L("Settings/Status/StarsLibraryCleared", "The Stars library and local categories were cleared."),
            TelemetryTaxonomy.Actions.ClearStarsLibrary,
            token => _diagnosticsService.ClearStarLibraryAsync(token),
            cancellationToken);

    public Task ExportDiagnosticsAsync(string destinationPath, CancellationToken cancellationToken = default) =>
        RunMutationAsync(
            L("Settings/Status/ExportingDiagnostics", "Exporting diagnostics..."),
            L("Settings/Status/DiagnosticsExported", "Diagnostics were exported."),
            TelemetryTaxonomy.Actions.ExportDiagnostics,
            token => _diagnosticsService.ExportDiagnosticsAsync(destinationPath, token),
            cancellationToken,
            refreshAfterSuccess: false);

    public void SelectTheme(string theme)
    {
        SelectedThemeOption = FindThemeOption(theme);
    }

    public void SelectPalette(string paletteId)
    {
        SelectedPaletteOption = FindPaletteOption(paletteId);
    }

    public void ResetPalette()
    {
        SelectPalette(ThemePaletteIds.JitHub);
    }

    public void ReportActionFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        StatusText = JitHub.WinUI.Helpers.UserFacingError.For(
            exception,
            JitHub.WinUI.Helpers.UserFacingErrorKind.Action,
            "settings");
        HasStatusError = true;
        TrackEvent("settings.error", new Dictionary<string, string?>
        {
            ["page"] = "settings",
            ["source"] = TelemetryTaxonomy.Sources.Dialog,
            ["error_kind"] = GetErrorKind(exception),
            ["result"] = TelemetryTaxonomy.Results.Error
        });
    }

    private async Task RunRefreshAsync(CancellationToken cancellationToken, string source)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            SettingsDiagnosticsSnapshot snapshot = await _diagnosticsService.GetSnapshotAsync(cancellationToken);
            Snapshot = snapshot;
            CanUseStoreTelemetry = snapshot.StoreTelemetry.IsAvailable;
            DiagnosticsEnabled = snapshot.DiagnosticsEnabled;
            StoreTelemetryEnabled = snapshot.StoreTelemetryEnabled;
            StatusText = L("Settings/Status/SnapshotCurrent", "Settings snapshot is current.");
            HasStatusError = false;
            stopwatch.Stop();
            TrackEvent("settings.loaded", new Dictionary<string, string?>
            {
                ["page"] = "settings",
                ["source"] = source,
                ["result"] = TelemetryTaxonomy.Results.Success,
                ["duration_bucket"] = TelemetrySanitizer.CreateDurationBucket(stopwatch.Elapsed)
            });
        }
        catch (Exception ex)
        {
            StatusText = JitHub.WinUI.Helpers.UserFacingError.For(
                ex,
                JitHub.WinUI.Helpers.UserFacingErrorKind.Refresh,
                "settings-diagnostics");
            HasStatusError = true;
            stopwatch.Stop();
            TrackEvent("settings.error", new Dictionary<string, string?>
            {
                ["page"] = "settings",
                ["source"] = source,
                ["error_kind"] = GetErrorKind(ex),
                ["result"] = TelemetryTaxonomy.Results.Error,
                ["duration_bucket"] = TelemetrySanitizer.CreateDurationBucket(stopwatch.Elapsed)
            });
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RunMutationAsync(
        string busyText,
        string successText,
        string action,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken,
        bool refreshAfterSuccess = true)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusText = busyText;
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            await operation(cancellationToken);

            if (refreshAfterSuccess)
            {
                Snapshot = await _diagnosticsService.GetSnapshotAsync(cancellationToken);
            }

            StatusText = successText;
            HasStatusError = false;
            stopwatch.Stop();
            TrackMutationOutcome(action, TelemetryTaxonomy.Results.Success, stopwatch.Elapsed);
        }
        catch (OperationCanceledException ex)
        {
            StatusText = L("Settings/Status/ActionCancelled", "Action cancelled.");
            HasStatusError = false;
            stopwatch.Stop();
            TrackMutationOutcome(action, TelemetryTaxonomy.Results.Cancelled, stopwatch.Elapsed);
            TrackSettingsError(action, ex, TelemetryTaxonomy.Results.Cancelled, stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            StatusText = JitHub.WinUI.Helpers.UserFacingError.For(
                ex,
                JitHub.WinUI.Helpers.UserFacingErrorKind.Action,
                "settings");
            HasStatusError = true;
            stopwatch.Stop();
            TrackMutationOutcome(action, TelemetryTaxonomy.Results.Error, stopwatch.Elapsed);
            TrackSettingsError(action, ex, TelemetryTaxonomy.Results.Error, stopwatch.Elapsed);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void NotifySelectedSectionChanged()
    {
        OnPropertyChanged(nameof(IsAppearanceSelected));
        OnPropertyChanged(nameof(IsGeneralSelected));
        OnPropertyChanged(nameof(IsPrivacySelected));
        OnPropertyChanged(nameof(IsDataCacheSelected));
        OnPropertyChanged(nameof(IsDiagnosticsSelected));
        OnPropertyChanged(nameof(IsAboutSelected));
    }

    private void NotifyThemeSelectionChanged()
    {
        OnPropertyChanged(nameof(IsSystemThemeSelected));
        OnPropertyChanged(nameof(IsLightThemeSelected));
        OnPropertyChanged(nameof(IsDarkThemeSelected));
    }

    private void UpdatePaletteSelection()
    {
        foreach (ThemePaletteOption option in PaletteOptions)
        {
            option.IsSelected = ReferenceEquals(option, _selectedPaletteOption);
        }

        OnPropertyChanged(nameof(CanResetPalette));
    }

    private void NotifySnapshotChanged()
    {
        OnPropertyChanged(nameof(StoreTelemetryStatusText));
        OnPropertyChanged(nameof(StoreTelemetryAvailabilityText));
        OnPropertyChanged(nameof(CacheDatabasePath));
        OnPropertyChanged(nameof(PayloadPath));
        OnPropertyChanged(nameof(ImagePath));
        OnPropertyChanged(nameof(DiagnosticsPath));
        OnPropertyChanged(nameof(StarLibraryPath));
        OnPropertyChanged(nameof(RepoFileCachePath));
        OnPropertyChanged(nameof(MetadataSizeText));
        OnPropertyChanged(nameof(PayloadSizeText));
        OnPropertyChanged(nameof(ImageSizeText));
        OnPropertyChanged(nameof(DiagnosticsSizeText));
        OnPropertyChanged(nameof(StarLibrarySizeText));
        OnPropertyChanged(nameof(RepoFileCacheSizeText));
        OnPropertyChanged(nameof(TotalCacheSizeText));
        OnPropertyChanged(nameof(SchemaVersionText));
    }

    private ThemeOption FindThemeOption(string? theme)
    {
        string normalizedTheme = NormalizeTheme(theme);
        foreach (ThemeOption option in ThemeOptions)
        {
            if (string.Equals(option.Value, normalizedTheme, StringComparison.Ordinal))
            {
                return option;
            }
        }

        return ThemeOptions[0];
    }

    private ThemePaletteOption FindPaletteOption(string? paletteId)
    {
        string normalized = ThemePaletteCatalog.Normalize(paletteId);
        foreach (ThemePaletteOption option in PaletteOptions)
        {
            if (string.Equals(option.Id, normalized, StringComparison.Ordinal))
            {
                return option;
            }
        }

        return PaletteOptions[0];
    }

    private IReadOnlyList<ThemePaletteOption> CreatePaletteOptions()
    {
        List<ThemePaletteOption> options = new(ThemePaletteCatalog.All.Count);
        foreach (ThemePaletteDefinition palette in ThemePaletteCatalog.All)
        {
            options.Add(new ThemePaletteOption(
                palette.Id,
                L($"Settings/Palette/{palette.ResourceKey}", palette.Name),
                L($"Settings/Palette/{palette.ResourceKey}Description", palette.Description),
                string.Equals(palette.Id, ThemePaletteIds.JitHub, StringComparison.Ordinal),
                palette.Light,
                palette.Dark));
        }

        return options;
    }

    private bool IsSelected(string sectionId) =>
        string.Equals(SelectedSection.Id, sectionId, StringComparison.Ordinal);

    private bool IsThemeSelected(string theme) =>
        string.Equals(SelectedThemeOption?.Value, theme, StringComparison.Ordinal);

    private static string NormalizeTheme(string? theme)
    {
        if (string.Equals(theme, ThemeConst.Light, StringComparison.Ordinal))
        {
            return ThemeConst.Light;
        }

        if (string.Equals(theme, ThemeConst.Dark, StringComparison.Ordinal))
        {
            return ThemeConst.Dark;
        }

        return ThemeConst.System;
    }

    private static string FormatBytes(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB"];
        double value = Math.Max(0, bytes);
        int suffixIndex = 0;
        while (value >= 1024 && suffixIndex < suffixes.Length - 1)
        {
            value /= 1024;
            suffixIndex++;
        }

        return suffixIndex == 0
            ? $"{value:0} {suffixes[suffixIndex]}"
            : $"{value:0.0} {suffixes[suffixIndex]}";
    }

    private void TrackEvent(string name, IReadOnlyDictionary<string, string?> properties)
    {
        try
        {
            _telemetryService.TrackEvent(name, properties);
        }
        catch
        {
            // Settings and privacy controls must remain usable if diagnostics are unavailable.
        }
    }

    private void ReportSettingPersistenceFailure(string action, Exception exception)
    {
        StatusText = JitHub.WinUI.Helpers.UserFacingError.For(
            exception,
            JitHub.WinUI.Helpers.UserFacingErrorKind.Action,
            "settings-persistence");
        HasStatusError = true;
        TrackMutationOutcome(action, TelemetryTaxonomy.Results.Error, TimeSpan.Zero);
        TrackSettingsError(action, exception, TelemetryTaxonomy.Results.Error, TimeSpan.Zero);
    }

    private void TrackMutationOutcome(string action, string result, TimeSpan duration) =>
        TrackEvent("settings.action.executed", new Dictionary<string, string?>
        {
            ["page"] = "settings",
            ["action"] = action,
            ["result"] = result,
            ["duration_bucket"] = TelemetrySanitizer.CreateDurationBucket(duration)
        });

    private void TrackSettingsError(string action, Exception exception, string result, TimeSpan duration) =>
        TrackEvent("settings.error", new Dictionary<string, string?>
        {
            ["page"] = "settings",
            ["action"] = action,
            ["error_kind"] = GetErrorKind(exception),
            ["result"] = result,
            ["duration_bucket"] = TelemetrySanitizer.CreateDurationBucket(duration)
        });

    private static Dictionary<string, string?> ToggleProperties(string action, bool enabled) => new()
    {
        ["page"] = "settings",
        ["action"] = action,
        ["result"] = enabled ? TelemetryTaxonomy.Results.Enabled : TelemetryTaxonomy.Results.Disabled
    };

    private static string GetErrorKind(Exception exception) => exception switch
    {
        OperationCanceledException => "canceled",
        UnauthorizedAccessException => "access_denied",
        IOException => "storage",
        _ => "unexpected"
    };

    private static IReadOnlyList<SettingsCacheOwnerItem> BuildCacheOwnerItems(
        IReadOnlyList<CacheOwnerSnapshot>? owners)
    {
        if (owners is null)
        {
            return [];
        }

        return owners.Select(owner =>
        {
            string caps = owner.Caps is { Count: > 0 }
                ? string.Join(", ", owner.Caps.Select(cap => $"{cap.Name} {FormatBytes(cap.Bytes)}"))
                : L("Settings/CacheOwners/NoEvictionCap", "No eviction cap");
            string size = LF("Settings/CacheOwners/PhysicalBytesFormat", "{0} physical", FormatBytes(owner.Bytes));
            if (owner.LogicalBytes > 0)
            {
                size += LF("Settings/CacheOwners/LogicalBytesFormat", " | {0} logical", FormatBytes(owner.LogicalBytes));
            }

            if (owner.OrphanBytes > 0)
            {
                size += LF("Settings/CacheOwners/OrphanBytesFormat", " | {0} orphaned", FormatBytes(owner.OrphanBytes));
            }

            return new SettingsCacheOwnerItem(
                owner.Id,
                owner.DisplayName,
                owner.Health.ToString(),
                size,
                LF("Settings/CacheOwners/DescriptionFormat", "{0}. TTL: {1}. Partition: {2}. {3}.", caps, owner.TtlPolicy, owner.AccountPartition, owner.ClearSemantics),
                owner.HealthDetail ?? string.Empty);
        }).ToArray();
    }

    private static string L(string key, string fallback) =>
        LocalizedResourceText.GetString(key, fallback);

    private static string LF(string key, string fallback, params object?[] arguments) =>
        LocalizedResourceText.Format(key, fallback, arguments);
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class SettingsCacheOwnerItem
{
    public SettingsCacheOwnerItem(
        string id,
        string displayName,
        string health,
        string size,
        string policy,
        string healthDetail)
    {
        Id = id;
        DisplayName = displayName;
        Health = health;
        Size = size;
        Policy = policy;
        HealthDetail = healthDetail;
    }

    public string Id { get; }

    public string DisplayName { get; }

    public string Health { get; }

    public string Size { get; }

    public string Policy { get; }

    public string HealthDetail { get; }
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class SettingsSectionItem
{
    public SettingsSectionItem(string id, string title, string glyph)
    {
        Id = id;
        Title = title;
        Glyph = glyph;
    }

    public string Id { get; }

    public string Title { get; }

    public string Glyph { get; }

    public string AutomationId => $"SettingsSection_{Id}";

    public override string ToString() => Title;
}

public sealed record ThemeOption(
    string Value,
    string Label,
    string Description);

public sealed partial class ThemePaletteOption : ObservableObject
{
    private bool _isSelected;

    public ThemePaletteOption(
        string id,
        string label,
        string description,
        bool isDefault,
        ThemePalettePreview light,
        ThemePalettePreview dark)
    {
        Id = id;
        Label = label;
        Description = description;
        IsDefault = isDefault;
        Light = light;
        Dark = dark;
    }

    public string Id { get; }

    public string Label { get; }

    public string Description { get; }

    public bool IsDefault { get; }

    public ThemePalettePreview Light { get; }

    public ThemePalettePreview Dark { get; }

    public string AutomationId => $"SettingsPalette_{Id}";

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

public sealed record SettingsCredit(
    string Name,
    string Role,
    string Description);

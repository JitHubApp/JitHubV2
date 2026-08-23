using System;
using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using JitHub.Models.Activities;
using JitHub.Models.GitHub;
using JitHub.Services;
using JitHub.WinUI.Helpers;
using JitHub.WinUI.ViewModels.Common;

namespace JitHub.WinUI.ViewModels.Pages;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class DashboardRepositoryCardItem : ObservableObject
{
    [ObservableProperty]
    public partial GitHubRepository Repository { get; set; } = new();

    public string FullName => Repository.FullName;

    public string Name => Repository.Name;

    public string Owner => Repository.Owner.Login;

    public string OwnerAvatarUrl => Repository.Owner.AvatarUrl ?? string.Empty;

    public string Description => string.IsNullOrWhiteSpace(Repository.Description)
        ? "No description provided."
        : Repository.Description!;

    public string Language => string.IsNullOrWhiteSpace(Repository.Language) ? "Unknown" : Repository.Language!;

    public string LanguageColor => RepositoryLanguageColorPalette.GetHex(Language);

    public string StarsText => RepositoryDisplayFormatter.FormatCount(Repository.StargazersCount);

    public string ForksText => RepositoryDisplayFormatter.FormatCount(Repository.ForksCount);

    public string UpdatedText => RepositoryDisplayFormatter.FormatRelativeUpdate(Repository.UpdatedAt);

    public string VisibilityText => Repository.Private ? "Private" : "Public";

    public string AutomationId => $"DashboardRepository_{Repository.Id}";

    public string AutomationName => $"Open repository {FullName}";

    public string Glyph => Repository.Private ? "\uE72E" : "\uE8B7";

    public ICommand? Command { get; set; }

    internal static string FormatCount(int value) => RepositoryDisplayFormatter.FormatCount(value);

    internal static string FormatRelativeTime(DateTimeOffset? value) =>
        RepositoryDisplayFormatter.FormatRelativeUpdate(value);

    partial void OnRepositoryChanged(GitHubRepository value)
    {
        OnPropertyChanged(nameof(FullName));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Owner));
        OnPropertyChanged(nameof(OwnerAvatarUrl));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(Language));
        OnPropertyChanged(nameof(LanguageColor));
        OnPropertyChanged(nameof(StarsText));
        OnPropertyChanged(nameof(ForksText));
        OnPropertyChanged(nameof(UpdatedText));
        OnPropertyChanged(nameof(VisibilityText));
        OnPropertyChanged(nameof(AutomationId));
        OnPropertyChanged(nameof(AutomationName));
        OnPropertyChanged(nameof(Glyph));
    }

}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class DashboardNotificationItem : ObservableObject
{
    public string Id { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Subtitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string MetaText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Glyph { get; set; } = "\uE8BD";

    [ObservableProperty]
    public partial bool IsUnread { get; set; }

    public ICommand? Command { get; set; }

    public string AutomationId => $"DashboardNotification_{Id}";

    public string AutomationName => $"Open notification {Title}";
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class DashboardQuickActionItem
{
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Subtitle { get; set; } = string.Empty;

    public string Glyph { get; set; } = "\uE8A7";

    public ActivityCardTone Tone { get; set; } = ActivityCardTone.Accent;

    public ICommand? Command { get; set; }

    public string AutomationId => $"DashboardQuickAction_{Id}";

    public string AutomationName => Title;
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class DashboardMetricViewItem : ObservableObject
{
    [ObservableProperty]
    public partial DashboardMetricItem Metric { get; set; } = new(string.Empty, string.Empty, string.Empty, "\uE8A7", CacheState.Miss);

    public string Label => Metric.Label;

    public string Value => Metric.Value;

    public string Caption => Metric.Caption;

    public string Glyph => Metric.Glyph;

    public string CacheStateText => Metric.CacheState.ToString();

    public string TransitionId => Metric.Id switch
    {
        DashboardMetricIds.Repositories => "DashboardOverviewMetricRepositories",
        DashboardMetricIds.Issues => "DashboardOverviewMetricIssues",
        DashboardMetricIds.PullRequests => "DashboardOverviewMetricPullRequests",
        DashboardMetricIds.Followers => "DashboardOverviewMetricFollowers",
        _ => string.Empty
    };

    partial void OnMetricChanged(DashboardMetricItem value)
    {
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(Caption));
        OnPropertyChanged(nameof(Glyph));
        OnPropertyChanged(nameof(CacheStateText));
        OnPropertyChanged(nameof(TransitionId));
    }
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class DashboardWidgetViewItem
{
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Subtitle { get; set; } = string.Empty;

    public string Glyph { get; set; } = "\uE8A7";

    public double Height { get; set; }

    public DashboardPageViewModel Dashboard { get; set; } = null!;

    public ICommand? ViewAllCommand { get; set; }

    public string AutomationId => $"DashboardWidget_{Id}";

    public string ViewAllAutomationId => $"DashboardWidgetViewAll_{Id}";

    public string ViewAllText => Id switch
    {
        DashboardWidgetIds.RecentActivity => "View all activities ->",
        DashboardWidgetIds.Repositories => "View all repositories ->",
        DashboardWidgetIds.Overview => "View profile ->",
        DashboardWidgetIds.RecommendedRepositories => "View all recommendations ->",
        DashboardWidgetIds.Notifications => "View all notifications ->",
        _ => "View all ->"
    };

    public bool IsRecentActivity => string.Equals(Id, DashboardWidgetIds.RecentActivity, StringComparison.Ordinal);

    public bool IsRepositories => string.Equals(Id, DashboardWidgetIds.Repositories, StringComparison.Ordinal);

    public bool IsQuickActions => string.Equals(Id, DashboardWidgetIds.QuickActions, StringComparison.Ordinal);

    public bool IsOverview => string.Equals(Id, DashboardWidgetIds.Overview, StringComparison.Ordinal);

    public bool IsRecommendedRepositories => string.Equals(Id, DashboardWidgetIds.RecommendedRepositories, StringComparison.Ordinal);

    public bool IsNotifications => string.Equals(Id, DashboardWidgetIds.Notifications, StringComparison.Ordinal);

    public bool HasViewAll => IsNotifications;
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class DashboardWidgetCustomizeItem : ObservableObject
{
    private bool _isVisible = true;
    private string _rail = "main";

    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Glyph { get; set; } = "\uE8A7";

    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }

    public string Rail
    {
        get => _rail;
        set
        {
            if (SetProperty(ref _rail, string.IsNullOrWhiteSpace(value) ? "main" : value))
            {
                OnPropertyChanged(nameof(RailLabel));
                OnPropertyChanged(nameof(ToggleRailLabel));
            }
        }
    }

    public string RailLabel => string.Equals(Rail, "side", StringComparison.Ordinal)
        ? "Side rail"
        : "Main rail";

    public string ToggleRailLabel => string.Equals(Rail, "side", StringComparison.Ordinal)
        ? "Move to main"
        : "Move to side";

    public ICommand? ToggleVisibilityCommand { get; set; }

    public ICommand? MoveUpCommand { get; set; }

    public ICommand? MoveDownCommand { get; set; }

    public ICommand? ToggleRailCommand { get; set; }

    public string AutomationId => $"DashboardCustomize_{Id}";

    public string VisibilityAutomationId => $"{AutomationId}_Visibility";

    public string MoveUpAutomationId => $"{AutomationId}_MoveUp";

    public string MoveDownAutomationId => $"{AutomationId}_MoveDown";

    public string ToggleRailAutomationId => $"{AutomationId}_ToggleRail";

    public string VisibilityAutomationName => $"Show {Title}";

    public string MoveUpAutomationName => $"Move {Title} up";

    public string MoveDownAutomationName => $"Move {Title} down";
}

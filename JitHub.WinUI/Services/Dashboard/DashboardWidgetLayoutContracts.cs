using System.Collections.Generic;

namespace JitHub.Services;

public static class DashboardWidgetIds
{
    public const string RecentActivity = "recent_activity";
    public const string Repositories = "repositories";
    public const string QuickActions = "quick_actions";
    public const string Overview = "overview";
    public const string RecommendedRepositories = "recommended_repositories";
    public const string Notifications = "notifications";

    public static IReadOnlyList<string> All { get; } =
    [
        RecentActivity,
        Repositories,
        QuickActions,
        Overview,
        RecommendedRepositories,
        Notifications
    ];
}

public sealed record DashboardWidgetLayout(
    int Version,
    IReadOnlyList<string> MainWidgetIds,
    IReadOnlyList<string> SideWidgetIds,
    IReadOnlyList<string> HiddenWidgetIds);

public interface IDashboardWidgetLayoutService
{
    DashboardWidgetLayout Load();

    void Save(DashboardWidgetLayout layout);

    DashboardWidgetLayout CreateDefault();

    DashboardWidgetLayout Normalize(DashboardWidgetLayout? layout);
}

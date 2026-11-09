using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace JitHub.Services;

public sealed class DashboardWidgetLayoutService : IDashboardWidgetLayoutService
{
    public const string SettingKey = "DashboardWidgetLayout.v1";
    private const int CurrentVersion = 1;
    private readonly ISettingService _settings;

    public DashboardWidgetLayoutService(ISettingService settings)
    {
        _settings = settings;
    }

    public DashboardWidgetLayout Load()
    {
        if (!_settings.Contains(SettingKey))
        {
            return CreateDefault();
        }

        string json = _settings.Get<string>(SettingKey);
        if (string.IsNullOrWhiteSpace(json))
        {
            return CreateDefault();
        }

        try
        {
            DashboardWidgetLayoutDto? dto = JsonSerializer.Deserialize(json, DashboardWidgetLayoutJsonContext.Default.DashboardWidgetLayoutDto);
            return Normalize(dto?.ToLayout());
        }
        catch (JsonException)
        {
            return CreateDefault();
        }
        catch (NotSupportedException)
        {
            return CreateDefault();
        }
    }

    public void Save(DashboardWidgetLayout layout)
    {
        DashboardWidgetLayout normalized = Normalize(layout);
        string json = JsonSerializer.Serialize(DashboardWidgetLayoutDto.FromLayout(normalized), DashboardWidgetLayoutJsonContext.Default.DashboardWidgetLayoutDto);
        _settings.Save(SettingKey, json);
    }

    public DashboardWidgetLayout CreateDefault() =>
        new(
            CurrentVersion,
            (string[])[DashboardWidgetIds.RecentActivity, DashboardWidgetIds.Repositories, DashboardWidgetIds.QuickActions],
            (string[])[DashboardWidgetIds.Overview, DashboardWidgetIds.RecommendedRepositories, DashboardWidgetIds.Notifications],
            (string[])[]);

    public DashboardWidgetLayout Normalize(DashboardWidgetLayout? layout)
    {
        if (layout is null)
        {
            return CreateDefault();
        }

        HashSet<string> allowed = new(DashboardWidgetIds.All, StringComparer.Ordinal);
        List<string> main = NormalizeList(layout.MainWidgetIds, allowed);
        List<string> side = NormalizeList(layout.SideWidgetIds, allowed);
        List<string> hidden = NormalizeList(layout.HiddenWidgetIds, allowed);
        HashSet<string> used = new(main.Concat(side).Concat(hidden), StringComparer.Ordinal);

        foreach (string id in DashboardWidgetIds.All)
        {
            if (used.Contains(id))
            {
                continue;
            }

            if (string.Equals(id, DashboardWidgetIds.Overview, StringComparison.Ordinal) ||
                string.Equals(id, DashboardWidgetIds.RecommendedRepositories, StringComparison.Ordinal) ||
                string.Equals(id, DashboardWidgetIds.Notifications, StringComparison.Ordinal))
            {
                side.Add(id);
            }
            else
            {
                main.Add(id);
            }
        }

        RemoveDuplicates(main, side, hidden);
        return new DashboardWidgetLayout(CurrentVersion, main, side, hidden);
    }

    private static List<string> NormalizeList(IReadOnlyList<string>? source, HashSet<string> allowed) =>
        (source ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id) && allowed.Contains(id))
            .Select(static id => id.Trim())
            .ToList();

    private static void RemoveDuplicates(params List<string>[] lists)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (List<string> list in lists)
        {
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (!seen.Add(list[i]))
                {
                    list.RemoveAt(i);
                }
            }
        }
    }
}

internal sealed class DashboardWidgetLayoutDto
{
    public int Version { get; set; }

    public List<string> MainWidgetIds { get; set; } = [];

    public List<string> SideWidgetIds { get; set; } = [];

    public List<string> HiddenWidgetIds { get; set; } = [];

    public DashboardWidgetLayout ToLayout() =>
        new(Version, MainWidgetIds, SideWidgetIds, HiddenWidgetIds);

    public static DashboardWidgetLayoutDto FromLayout(DashboardWidgetLayout layout) =>
        new()
        {
            Version = layout.Version,
            MainWidgetIds = layout.MainWidgetIds.ToList(),
            SideWidgetIds = layout.SideWidgetIds.ToList(),
            HiddenWidgetIds = layout.HiddenWidgetIds.ToList()
        };
}

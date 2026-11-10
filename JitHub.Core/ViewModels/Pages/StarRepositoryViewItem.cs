using System;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using JitHub.Models.GitHub;
using JitHub.Services;
using JitHub.WinUI.Helpers;
using JitHub.WinUI.ViewModels.Common;

namespace JitHub.WinUI.ViewModels.Pages;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class StarRepositoryViewItem : ObservableObject
{
    private StarLibraryItem _item = null!;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial bool IsSelectionModeVisible { get; set; }

    public StarLibraryItem Item
    {
        get => _item;
        private set => SetProperty(ref _item, value);
    }

    public GitHubRepository Repository => Item.Repository;
    public string Key => Item.Key;
    public string FullName => Repository.FullName;
    public string Name => Repository.Name;
    public string Owner => Repository.Owner.Login;
    public string OwnerAvatarUrl => Repository.Owner.AvatarUrl ?? string.Empty;
    public string Description => string.IsNullOrWhiteSpace(Repository.Description) ? "No description provided." : Repository.Description!;
    public string Language => string.IsNullOrWhiteSpace(Repository.Language) ? "Unknown" : Repository.Language!;
    public string StarsText => RepositoryDisplayFormatter.FormatCount(Repository.StargazersCount);
    public string ActivityText => RepositoryDisplayFormatter.FormatRelativeUpdate(Repository.PushedAt ?? Repository.UpdatedAt);
    public string StarredText => Item.StarredAt == DateTimeOffset.MinValue ? "Starred" : $"Starred {FormatRelative(Item.StarredAt)}";
    public string CategoriesText => Item.Categories.Count == 0 ? "Uncategorized" : string.Join(" · ", Item.Categories.Select(static category => category.Name));
    public string StateText => Repository.Archived ? "Archived" : Repository.Fork ? "Fork" : Repository.Private ? "Private" : string.Empty;
    public bool HasState => !string.IsNullOrWhiteSpace(StateText);
    public string LanguageColor => RepositoryLanguageColorPalette.GetHex(Language);
    public string AutomationId => "StarsRepository_" + Repository.Id.ToString(CultureInfo.InvariantCulture);
    public string AutomationName => $"{FullName}, {Language}, {StarsText} stars, {StarredText}";
    public string HoverUnstarAutomationId => "StarsHoverUnstar_" + Repository.Id.ToString(CultureInfo.InvariantCulture);
    public string HoverMenuAutomationId => "StarsHoverMenu_" + Repository.Id.ToString(CultureInfo.InvariantCulture);
    public string SelectionCheckBoxAutomationId => "StarsSelectRepository_" + Repository.Id.ToString(CultureInfo.InvariantCulture);
    public string DragHandleAutomationId => "StarsDragHandle_" + Repository.Id.ToString(CultureInfo.InvariantCulture);
    public string SelectionCheckBoxAutomationName => $"Select {FullName}";

    public static StarRepositoryViewItem FromItem(StarLibraryItem item) => new() { Item = item };

    public bool UpdateFrom(StarLibraryItem item)
    {
        Item = item;
        OnPropertyChanged(string.Empty);
        return true;
    }

    private static string FormatRelative(DateTimeOffset value)
    {
        TimeSpan age = DateTimeOffset.Now - value.ToLocalTime();
        if (age.TotalDays < 1) return "today";
        if (age.TotalDays < 7) return $"{Math.Max(1, (int)age.TotalDays)}d ago";
        return value.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.CurrentCulture);
    }

}

using System;
using System.Collections.Generic;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using JitHub.Models.GitHub;
using JitHub.Models.NavArgs;
using JitHub.Services;

namespace JitHub.WinUI.ViewModels.Pages;

public enum ShellCommandSearchResultKind
{
    Command,
    Repository,
    SearchQuery
}

public enum ShellRepositoryFilter
{
    Public,
    Private,
    Forked
}

public sealed partial class ShellNavigationItem : ObservableObject
{
    private string _badgeText = string.Empty;
    private int _badgeValue;
    private bool _isEnabled = true;
    private bool _isSelected;

    public ShellNavigationItem(string id, string label, string glyph, ICommand command)
    {
        Id = id;
        Label = label;
        Glyph = glyph;
        Command = command;
    }

    public string Id { get; }

    public string Label { get; }

    public string Glyph { get; }

    public string AutomationId => $"ShellNav_{Id}";

    public string BadgeText
    {
        get => _badgeText;
        set
        {
            if (SetProperty(ref _badgeText, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasBadgeSuffix));
                OnPropertyChanged(nameof(BadgeAutomationName));
            }
        }
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                OnPropertyChanged(nameof(SelectionStatus));
            }
        }
    }

    public int BadgeValue
    {
        get => _badgeValue;
        set
        {
            if (SetProperty(ref _badgeValue, Math.Max(0, value)))
            {
                OnPropertyChanged(nameof(HasBadge));
                OnPropertyChanged(nameof(DisplayBadgeValue));
                OnPropertyChanged(nameof(BadgeAutomationName));
            }
        }
    }

    public bool HasBadge => BadgeValue > 0;

    public int DisplayBadgeValue => Math.Min(99, BadgeValue);

    public bool HasBadgeSuffix => BadgeText.EndsWith('+');

    public string BadgeAutomationName => Id == "notifications"
        ? HasBadge
            ? $"{BadgeText} unread notifications"
            : "No unread notifications"
        : HasBadge
            ? $"{BadgeText} {Label.ToLowerInvariant()}"
            : $"No {Label.ToLowerInvariant()}";

    public string SelectionStatus => IsSelected ? "Selected" : "Not selected";

    public ICommand Command { get; }
}

public sealed partial class ShellRepositoryItem : ObservableObject
{
    private bool _isSelected;

    public ShellRepositoryItem(GitHubRepository repository, Action<GitHubRepository> command)
    {
        Repository = repository;
        Command = new CommunityToolkit.Mvvm.Input.RelayCommand(() => command(Repository));
    }

    public ShellRepositoryItem(GitHubRepository repository, ICommand command)
    {
        Repository = repository;
        Command = command;
    }

    public GitHubRepository Repository { get; private set; }

    public string Key => RepositoryLibraryProjection.RepositoryKey(Repository);

    public string FullName => Repository.FullName;

    public string AutomationId => $"ShellRepo_{SanitizeAutomationId(Repository.FullName)}";

    public string AutomationName => $"Open repository {Repository.FullName}";

    public string Name => Repository.Name;

    public string Owner => Repository.Owner.Login;

    public string Description => Repository.Description ?? string.Empty;

    public bool IsPrivate => Repository.Private;

    public bool IsFork => Repository.Fork;

    public bool IsArchived => Repository.Archived;

    public string VisibilityLabel => IsPrivate ? "Private" : "Public";

    public string RepositoryKindLabel => IsFork ? "Forked" : VisibilityLabel;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public ICommand Command { get; }

    public bool Update(GitHubRepository repository)
    {
        if (ReferenceEquals(Repository, repository))
        {
            return false;
        }

        Repository = repository;
        OnPropertyChanged(nameof(Key));
        OnPropertyChanged(nameof(FullName));
        OnPropertyChanged(nameof(AutomationId));
        OnPropertyChanged(nameof(AutomationName));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Owner));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(IsPrivate));
        OnPropertyChanged(nameof(IsFork));
        OnPropertyChanged(nameof(IsArchived));
        OnPropertyChanged(nameof(VisibilityLabel));
        OnPropertyChanged(nameof(RepositoryKindLabel));
        return true;
    }

    private static string SanitizeAutomationId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        Span<char> buffer = stackalloc char[value.Length];
        for (int i = 0; i < value.Length; i++)
        {
            char ch = value[i];
            buffer[i] = char.IsLetterOrDigit(ch) ? ch : '_';
        }

        return buffer.ToString();
    }
}

public sealed class ShellCommandSearchResult
{
    public ShellCommandSearchResult(
        ShellCommandSearchResultKind kind,
        string title,
        string subtitle,
        string glyph,
        int score,
        ICommand command,
        object? payload = null)
    {
        Kind = kind;
        Title = title ?? string.Empty;
        Subtitle = subtitle ?? string.Empty;
        Glyph = glyph;
        Score = score;
        Command = command;
        Payload = payload;
    }

    public ShellCommandSearchResultKind Kind { get; }

    public string Title { get; }

    public string Subtitle { get; }

    public string Glyph { get; }

    public int Score { get; }

    public ICommand Command { get; }

    public object? Payload { get; }

    public string AutomationId => $"ShellSearchResult_{Kind}_{CreateStableAutomationToken(Title)}";

    public string KindLabel => Kind switch
    {
        ShellCommandSearchResultKind.Command => "Command",
        ShellCommandSearchResultKind.Repository => "Repository",
        ShellCommandSearchResultKind.SearchQuery => "Search",
        _ => "Result"
    };

    public string AutomationName => string.IsNullOrWhiteSpace(Title)
        ? string.IsNullOrWhiteSpace(Subtitle) ? $"{KindLabel} result" : Subtitle.Trim()
        : string.IsNullOrWhiteSpace(Subtitle) ? Title.Trim() : $"{Title.Trim()}, {Subtitle.Trim()}";

    private static string CreateStableAutomationToken(string value)
    {
        const ulong offsetBasis = 14695981039346656037;
        const ulong prime = 1099511628211;
        ulong hash = offsetBasis;
        Span<char> readable = stackalloc char[Math.Min(value.Length, 32)];
        int length = 0;

        foreach (char character in value)
        {
            hash ^= character;
            hash *= prime;
            if (length < readable.Length)
            {
                readable[length++] = char.IsLetterOrDigit(character) ? character : '_';
            }
        }

        string prefix = length == 0 ? "Result" : readable[..length].ToString().Trim('_');
        if (string.IsNullOrEmpty(prefix))
        {
            prefix = "Result";
        }

        return $"{prefix}_{hash:X16}";
    }
}

public sealed record ShellWorkspaceTabIdentity(string Key, string Page)
{
    public static ShellWorkspaceTabIdentity Home() => new("home", "home");

    public static ShellWorkspaceTabIdentity Settings() => new("settings", "settings");

    public static ShellWorkspaceTabIdentity Profile() => new("profile", "profile");

    public static ShellWorkspaceTabIdentity Profile(string login)
    {
        string normalized = Normalize(login);
        return string.IsNullOrWhiteSpace(normalized)
            ? Profile()
            : new ShellWorkspaceTabIdentity($"profile:{normalized}", "profile");
    }

    public static ShellWorkspaceTabIdentity DesignLab() => new("design-lab", "design-lab");

    public static ShellWorkspaceTabIdentity ManageRepositories() => new("manage-repositories", "manage-repositories");

    public static ShellWorkspaceTabIdentity Search(string query)
    {
        string normalized = Normalize(query);
        return new ShellWorkspaceTabIdentity($"search:{normalized}", "search");
    }

    public static ShellWorkspaceTabIdentity Repository(GitHubRepository repository, RepoPageType page, string? branch = null)
    {
        string fullName = Normalize(repository.FullName);
        string branchSegment = string.IsNullOrWhiteSpace(branch) ? string.Empty : $":{Normalize(branch)}";
        return new ShellWorkspaceTabIdentity($"repo:{fullName}:{page}{branchSegment}", PageName(page));
    }

    public static ShellWorkspaceTabIdentity Repository(string fullName, RepoPageType page, string? branch = null)
    {
        string normalizedFullName = Normalize(fullName);
        string branchSegment = string.IsNullOrWhiteSpace(branch) ? string.Empty : $":{Normalize(branch)}";
        return new ShellWorkspaceTabIdentity($"repo:{normalizedFullName}:{page}{branchSegment}", PageName(page));
    }

    public static string PageName(RepoPageType page) => page switch
    {
        RepoPageType.IssuePage => "issues",
        RepoPageType.PullRequestPage => "pull-requests",
        RepoPageType.CommitPage => "commits",
        _ => "code"
    };

    public static string NavigationItemId(string? page) => page switch
    {
        "home" => "home",
        "issues" => "issues",
        "pull-requests" => "pull-requests",
        "notifications" => "notifications",
        "stars" => "stars",
        "gists" => "gists",
        "settings" => "settings",
        _ => string.Empty
    };

    public static string Normalize(string? value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant();
}

public sealed record ShellRouteEntry(
    ShellWorkspaceTabIdentity Identity,
    string Header,
    Type PageSource,
    object? Parameter,
    ShellRouteViewState? ViewState = null);

public sealed record ShellRouteViewState(
    int? SelectedIndex,
    double VerticalOffset,
    double HorizontalOffset,
    string? SelectionTargetId = null,
    string? ScrollTargetId = null,
    string? FocusTargetId = null);

public sealed class ShellRouteHistory
{
    private readonly List<ShellRouteEntry> _entries = [];
    private int _index = -1;

    public int Count => _entries.Count;

    public int CurrentIndex => _index;

    public bool CanGoBack => _index > 0;

    public bool CanGoForward => _index >= 0 && _index < _entries.Count - 1;

    public ShellRouteEntry? Current =>
        _index >= 0 && _index < _entries.Count ? _entries[_index] : null;

    public void Push(ShellRouteEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (CanGoForward)
        {
            _entries.RemoveRange(_index + 1, _entries.Count - _index - 1);
        }

        _entries.Add(entry);
        _index = _entries.Count - 1;
    }

    public bool UpdateCurrentViewState(ShellRouteViewState viewState)
    {
        ArgumentNullException.ThrowIfNull(viewState);
        if (_index < 0 || _index >= _entries.Count)
        {
            return false;
        }

        _entries[_index] = _entries[_index] with { ViewState = viewState };
        return true;
    }

    public bool TryGoBack(out ShellRouteEntry? entry)
    {
        if (!CanGoBack)
        {
            entry = null;
            return false;
        }

        _index--;
        entry = _entries[_index];
        return true;
    }

    public bool TryGoForward(out ShellRouteEntry? entry)
    {
        if (!CanGoForward)
        {
            entry = null;
            return false;
        }

        _index++;
        entry = _entries[_index];
        return true;
    }
}

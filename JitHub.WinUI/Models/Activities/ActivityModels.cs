using System;
using System.Collections.Generic;
using System.Windows.Input;
using JitHub.Models.GitHub;

namespace JitHub.Models.Activities;

public enum ActivityNavigationTargetKind
{
    Repository,
    Issue,
    PullRequest,
    Commit,
    UnsupportedTodo
}

public enum ActivityCardTone
{
    Neutral,
    Accent,
    Success,
    Warning,
    Danger,
    Gold,
    Purple
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class ActivityNavigationTarget
{
    public ActivityNavigationTargetKind Kind { get; set; }

    public string Label { get; set; } = string.Empty;

    public string RepositoryFullName { get; set; } = string.Empty;

    public string? Branch { get; set; }

    public int Number { get; set; }

    public string? Sha { get; set; }

    public string? UnsupportedReason { get; set; }

    public GitHubRepository? Repository { get; set; }
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class ActivityCardActionViewModel
{
    public string Label { get; set; } = string.Empty;

    public string Glyph { get; set; } = "\uE8A7";

    public ActivityNavigationTarget Target { get; set; } = new();

    public ICommand? Command { get; set; }
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class ActivityCardDetailViewModel
{
    public string Text { get; set; } = string.Empty;

    public string Glyph { get; set; } = "\uE8A7";

    public bool IsEmphasized { get; set; }
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class ActivitySentencePartViewModel
{
    public string Text { get; set; } = string.Empty;

    public string Glyph { get; set; } = string.Empty;

    public ActivityNavigationTarget? Target { get; set; }

    public ICommand? Command { get; set; }

    public bool IsEmphasized { get; set; }

    public bool IsAction => Target is not null
        && Target.Kind != ActivityNavigationTargetKind.UnsupportedTodo
        && Command is not null;
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class ActivityCardViewModel
{
    public string EventId { get; set; } = string.Empty;

    public string EventType { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Subtitle { get; set; } = string.Empty;

    public string ActorLogin { get; set; } = string.Empty;

    public string? ActorAvatarUrl { get; set; }

    public string RepoDisplayName { get; set; } = string.Empty;

    public string TimestampText { get; set; } = string.Empty;

    public string Glyph { get; set; } = "\uE8A7";

    public ActivityCardTone Tone { get; set; }

    public List<ActivitySentencePartViewModel> SentenceParts { get; set; } = [];

    public List<ActivityCardDetailViewModel> Details { get; set; } = [];

    public List<ActivityCardActionViewModel> Actions { get; set; } = [];

    public List<ActivityNavigationTarget> UnsupportedTodos { get; set; } = [];

    public bool HasDetails => Details.Count > 0;

    public bool HasActions => Actions.Count > 0;
}

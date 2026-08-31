using JitHub.Models;

namespace JitHub.Web.Content;

public sealed record MediaAsset(
    string Id,
    string LightSource,
    string DarkSource,
    int Width,
    int Height,
    string Alt,
    string Caption);

internal sealed record ShowcaseChapter(
    string Id,
    string Eyebrow,
    string Title,
    string Description,
    IReadOnlyList<string> Highlights,
    MediaAsset? FeaturedMedia = null);

internal sealed record CapabilityItem(
    string ReleaseSurfaceId,
    string Title,
    string Description);

internal sealed record CapabilityGroup(
    string Id,
    string Title,
    IReadOnlyList<CapabilityItem> Items);

internal sealed record ThemePaletteStory(
    string Id,
    string Name,
    string Description,
    ThemePalettePreview Light,
    ThemePalettePreview Dark);

internal static class LandingPageContent
{
    internal const int ReleaseSurfaceCount = 22;

    internal static readonly MediaAsset HomeWorkspace = new(
        "home-workspace",
        "media/showcase/home-workspace-light.png",
        "media/showcase/home-workspace-dark.png",
        3200,
        1800,
        "JitHub's customizable Home workspace with global search, navigation, repository rail, overview, and activity widgets.",
        "Recent work, repositories, and account activity in one Home view.");

    internal static readonly MediaAsset PullRequestConversation = new(
        "pull-request-conversation",
        "media/showcase/pull-request-conversation-light.png",
        "media/showcase/pull-request-conversation-dark.png",
        3200,
        1800,
        "A JitHub pull request conversation with navigation, Markdown, reactions, comments, and review actions.",
        "A pull request with its discussion and review context together.");

    internal static readonly MediaAsset CodeEditor = new(
        "code-editor",
        "media/showcase/code-editor-light.png",
        "media/showcase/code-editor-dark.png",
        3200,
        1800,
        "JitHub's repository code workspace with file navigation, breadcrumbs, branches, and a native source editor.",
        "Repository navigation and source editing share the same workspace.");

    internal static readonly MediaAsset CsvTable = new(
        "csv-table",
        "media/showcase/csv-table-light.png",
        "media/showcase/csv-table-dark.png",
        3200,
        1800,
        "A CSV file rendered as a sortable, virtualized native data table in JitHub.",
        "CSV and TSV files open as sortable tables, with the original text still available.");

    internal static readonly MediaAsset CommitDiff = new(
        "commit-diff",
        "media/showcase/commit-diff-light.png",
        "media/showcase/commit-diff-dark.png",
        3200,
        1800,
        "JitHub's commit workspace with history, changed-file tree, virtualized diff, comments, checks, and compare tools.",
        "Changed files, the diff, checks, and discussion stay connected.");

    internal static readonly MediaAsset StarsLibrary = new(
        "stars-library",
        "media/showcase/stars-library-light.png",
        "media/showcase/stars-library-dark.png",
        3200,
        1800,
        "The JitHub Stars library with smart lists, colored categories, search, sorting, and repository actions.",
        "Stars can be searched, sorted, and grouped into categories.");

    internal static readonly MediaAsset GistsEditor = new(
        "gists-editor",
        "media/showcase/gists-editor-light.png",
        "media/showcase/gists-editor-dark.png",
        3200,
        1800,
        "JitHub's Gists workspace with a searchable library, file detail, Markdown, and editing actions.",
        "Browse a Gist, inspect its files, and edit it without leaving JitHub.");

    internal static readonly MediaAsset ProfileOverview = new(
        "profile-overview",
        "media/showcase/profile-overview-light.png",
        "media/showcase/profile-overview-dark.png",
        3200,
        1800,
        "A JitHub profile overview with identity details, contribution history, and pinned repositories.",
        "Identity, contribution history, and pinned repositories in one profile.");

    internal static IReadOnlyList<MediaAsset> AllMedia { get; } =
    [
        HomeWorkspace,
        PullRequestConversation,
        CodeEditor,
        CsvTable,
        CommitDiff,
        StarsLibrary,
        GistsEditor,
        ProfileOverview
    ];

    internal static IReadOnlyList<ShowcaseChapter> Chapters { get; } =
    [
        new(
            "home",
            "Home and workspace",
            "Start with what needs your attention.",
            "Home keeps recent activity, repositories, account overview, and common actions in one place. Choose the widgets that are useful to you and leave the rest out.",
            [
                "Search repositories and commands from anywhere in the app.",
                "Arrange activity, recent repositories, and account overview around the way you work.",
                "Keep frequently used repositories nearby in the collapsible navigation rail."
            ]),
        new(
            "collaboration",
            "Issues and pull requests",
            "Read the thread. Review the change.",
            "Issues and pull requests keep the conversation, files, commits, reviews, and status together. Personal queues and repository workspaces follow the same familiar shape.",
            [
                "Move between conversation, files, commits, and reviews without rebuilding context.",
                "Reply, react, quote, edit, pin, hide, and copy links or Markdown from comments.",
                "Review and merge with checks and repository status close by."
            ],
            PullRequestConversation),
        new(
            "code",
            "Code and rich files",
            "Browse a repository without losing context.",
            "Move through branches and files, read Markdown, inspect CSV or SVG content, and edit source from the same repository view.",
            [
                "Browse branches and file trees with breadcrumbs that keep the current path clear.",
                "Read Markdown, SVG, CSV, and TSV files in views made for their content.",
                "Use the native editor or switch back to the original source whenever you need it."
            ]),
        new(
            "commits",
            "Commits and changes",
            "See what changed, file by file.",
            "Commit history leads into a focused diff with changed files, checks, and discussion nearby. Branch comparison and search are available when the change is larger.",
            [
                "Scan history with filters available when the list needs narrowing.",
                "Use the changed-file tree to move through or collapse parts of a larger diff.",
                "Search and copy changes, inspect checks, compare branches, and discuss a commit."
            ],
            CommitDiff),
        new(
            "library",
            "Libraries and identity",
            "Keep Stars, Gists, and people close.",
            "Organize saved repositories, work with Gists, check profiles and contributions, triage notifications, and manage the account behind it all.",
            [
                "Group Stars with smart lists and colored categories, with search and bulk actions when needed.",
                "Create and edit multi-file Gists alongside their rendered Markdown.",
                "Move through profiles, notifications, repositories, and settings without leaving the app."
            ])
    ];

    internal static IReadOnlyList<ThemePaletteStory> ThemePalettes { get; } =
        ThemePaletteCatalog.All
            .Select(static palette => new ThemePaletteStory(
                palette.Id,
                palette.Name,
                palette.Description,
                palette.Light,
                palette.Dark))
            .ToArray();

    internal static IReadOnlyList<ThemePaletteStory> FeaturedThemePalettes { get; } =
    [
        FindThemePalette(ThemePaletteIds.JitHub),
        FindThemePalette(ThemePaletteIds.Windows11),
        FindThemePalette(ThemePaletteIds.OneDarkPro),
        FindThemePalette(ThemePaletteIds.Dracula)
    ];

    internal static IReadOnlyList<ThemePaletteStory> AdditionalThemePalettes { get; } =
        ThemePalettes
            .Where(palette => !FeaturedThemePalettes.Any(featured =>
                string.Equals(featured.Id, palette.Id, StringComparison.Ordinal)))
            .ToArray();

    internal static IReadOnlyList<CapabilityGroup> CapabilityGroups { get; } =
    [
        new(
            "foundation",
            "Native foundation",
            [
                new("REL-UI-001", "Sign in", "Secure OAuth handoff, retry, cancellation, and sanitized failures."),
                new("REL-UI-002", "Shell", "Persistent navigation, repository rail, history, search, and title-bar actions."),
                new("REL-UI-003", "Home", "Recent work, customizable widgets, repository navigation, and account overview."),
                new("REL-UI-006", "Notifications", "Unread, read, done, subscription controls, badges, and internal routing."),
                new("REL-UI-017", "Settings", "Live color themes, telemetry, cache, diagnostics, export, clearing, and About."),
                new("REL-UI-019", "Dialogs and flyouts", "Predictable sizing, focus restoration, validation, and light dismiss.")
            ]),
        new(
            "collaboration-capabilities",
            "Collaboration",
            [
                new("REL-UI-004", "My Issues", "State, scope, filters, search, detail, editing, comments, and reactions."),
                new("REL-UI-005", "My Pull Requests", "Personal review queues, sections, replies, reactions, review, and merge."),
                new("REL-UI-013", "Repository Issues", "Issue lists, filters, details, metadata, state, and comments."),
                new("REL-UI-014", "Repository Pull Requests", "Conversation, files, commits, reviews, timeline, and merge workflows.")
            ]),
        new(
            "code-capabilities",
            "Code and history",
            [
                new("REL-UI-011", "Repository workspace", "Identity, branches, actions, tabs, and repository controls."),
                new("REL-UI-012", "Repository code", "Tree, breadcrumbs, branches, previews, find, outline, and large files."),
                new("REL-UI-015", "Repository commits", "History, file trees, diffs, compare, search, copy, comments, and checks."),
                new("REL-UI-016", "Repository search", "Typed results, filters, sorting, paging, and internal navigation."),
                new("REL-UI-018", "Native Markdown", "Tables, tasks, code, images, SVG, links, selection, and copy."),
                new("REL-UI-020", "CSV and TSV", "Frozen headers, virtualization, sorting, resizing, selection, copy, and UIA."),
                new("REL-UI-021", "SVG viewport", "Secure rendering, cancellation, tiling, DPI support, and 0.1x to 8x zoom."),
                new("REL-UI-022", "Code editor", "Highlighting, line numbers, find, go-to-line, wrapping, selection, and copy.")
            ]),
        new(
            "library-capabilities",
            "Libraries and account",
            [
                new("REL-UI-007", "Stars", "Smart lists, categories, colors, search, bulk actions, sync, and offline use."),
                new("REL-UI-008", "Gists", "Paged library, file detail, create, edit, delete, and Markdown."),
                new("REL-UI-009", "Profiles", "Authenticated and public profiles, lazy sections, edit, follow, and routing."),
                new("REL-UI-010", "Repositories", "Filters, search, sorting, paging, creation, deletion, and offline state.")
            ])
    ];

    private static ThemePaletteStory FindThemePalette(string id) =>
        ThemePalettes.Single(palette => string.Equals(palette.Id, id, StringComparison.Ordinal));
}

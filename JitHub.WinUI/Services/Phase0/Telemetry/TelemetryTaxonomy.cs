using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace JitHub.Services;

public static class TelemetryTaxonomy
{
    public static class Results
    {
        public const string AlreadyGranted = "already_granted";
        public const string Authenticated = "authenticated";
        public const string Started = "started";
        public const string Success = "success";
        public const string Empty = "empty";
        public const string Partial = "partial";
        public const string Error = "error";
        public const string AuthError = "auth_error";
        public const string Cancelled = "cancelled";
        public const string CachedError = "cached_error";
        public const string Opened = "opened";
        public const string Unavailable = "unavailable";
        public const string NetworkError = "network_error";
        public const string IdentityError = "identity_error";
        public const string IdentityUnavailable = "identity_unavailable";
        public const string Rejected = "rejected";
        public const string Launched = "launched";
        public const string Deferred = "deferred";
        public const string NoSession = "no_session";
        public const string Disabled = "disabled";
        public const string Enabled = "enabled";
        public const string Failed = "failed";
        public const string PermissionDenied = "permission_denied";
        public const string Queued = "queued";
        public const string Expanded = "expanded";
        public const string Collapsed = "collapsed";
    }

    public static class Sources
    {
        public const string Action = "action";
        public const string Callback = "callback";
        public const string Cache = "cache";
        public const string Dwell = "dwell";
        public const string Dialog = "dialog";
        public const string Full = "full";
        public const string Hover = "hover";
        public const string Initial = "initial";
        public const string Incremental = "incremental";
        public const string List = "list";
        public const string Login = "login";
        public const string Navigation = "navigation";
        public const string NavigationHandoff = "navigation_handoff";
        public const string Neighbor = "neighbor";
        public const string Notifications = "notifications";
        public const string Query = "query";
        public const string Refresh = "refresh";
        public const string Route = "route";
        public const string Shell = "shell";
        public const string SignIn = "sign_in";
        public const string Scope = "scope";
        public const string Startup = "startup";
        public const string User = "user";
    }

    public static class Actions
    {
        public const string Add = "add";
        public const string BreadcrumbPath = "breadcrumb_path";
        public const string BreadcrumbRoot = "breadcrumb_root";
        public const string ClearAllCache = "clear_all_cache";
        public const string ClearDiagnostics = "clear_diagnostics";
        public const string ClearImageCache = "clear_image_cache";
        public const string ClearQueryCache = "clear_query_cache";
        public const string ClearRepoFileCache = "clear_repo_file_cache";
        public const string ClearStarsLibrary = "clear_stars_library";
        public const string Comment = "comment";
        public const string CommentDelete = "comment_delete";
        public const string CommentEdit = "comment_edit";
        public const string CommentHide = "comment_hide";
        public const string CommentPin = "comment_pin";
        public const string CommentReaction = "comment_reaction";
        public const string CommentUnhide = "comment_unhide";
        public const string CommentUnpin = "comment_unpin";
        public const string Close = "close";
        public const string CopyCode = "copy_code";
        public const string CopyFact = "copy_fact";
        public const string CopyDiff = "copy_diff";
        public const string CopyLineLink = "copy_line_link";
        public const string CopyLink = "copy_link";
        public const string CopyMarkdown = "copy_markdown";
        public const string CopyPath = "copy_path";
        public const string CopyRaw = "copy_raw";
        public const string CopySelection = "copy_selection";
        public const string CsvCopy = "csv_copy";
        public const string CsvPlainView = "csv_plain_view";
        public const string CsvReorder = "csv_reorder";
        public const string CsvResize = "csv_resize";
        public const string CsvRichView = "csv_rich_view";
        public const string CsvSort = "csv_sort";
        public const string Create = "create";
        public const string Delete = "delete";
        public const string Drawer = "drawer";
        public const string Diagnostics = "diagnostics";
        public const string DeveloperMode = "developer_mode";
        public const string Edit = "edit";
        public const string ExportDiagnostics = "export_diagnostics";
        public const string ExternalOpen = "external_open";
        public const string Find = "find";
        public const string Follow = "follow";
        public const string Hydrate = "hydrate";
        public const string MarkAllRead = "mark_all_read";
        public const string MarkRead = "mark_read";
        public const string LoadRemoteImages = "load_remote_images";
        public const string Metadata = "metadata";
        public const string Merge = "merge";
        public const string Mute = "mute";
        public const string Outline = "outline";
        public const string OpenRepository = "open_repository";
        public const string OpenProfileExternal = "open_profile_external";
        public const string OpenLink = "open_link";
        public const string QuoteReply = "quote_reply";
        public const string Reaction = "reaction";
        public const string Render = "render";
        public const string Remove = "remove";
        public const string Reopen = "reopen";
        public const string Reorder = "reorder";
        public const string Retry = "retry";
        public const string ReviewApprove = "review_approve";
        public const string ReviewComment = "review_comment";
        public const string ReviewReply = "review_reply";
        public const string ReviewRequestChanges = "review_request_changes";
        public const string StoreTelemetry = "store_telemetry";
        public const string SectionChanged = "section_changed";
        public const string SelectionMode = "selection_mode";
        public const string SignIn = "sign_in";
        public const string SignOut = "sign_out";
        public const string SyncStar = "sync_star";
        public const string SyncUnstar = "sync_unstar";
        public const string ThemeChanged = "theme_changed";
        public const string ToggleState = "toggle_state";
        public const string ToggleDetails = "toggle_details";
        public const string Unfollow = "unfollow";
        public const string Unstar = "unstar";
        public const string UndoUnstar = "undo_unstar";
        public const string Unmute = "unmute";
        public const string Unsubscribe = "unsubscribe";
        public const string Update = "update";
        public const string RefreshUser = "refresh_user";
    }

    public static class FilterTypes
    {
        public const string All = "all";
        public const string Participating = "participating";
        public const string Unread = "unread";
    }

    public static class ErrorKinds
    {
        public const string Api = "api";
        public const string Authentication = "authentication";
        public const string Cancelled = "cancelled";
        public const string InvalidCallback = "invalid_callback";
        public const string Io = "io";
        public const string Launch = "launch";
        public const string Network = "network";
        public const string Unexpected = "unexpected";
    }

    public static string CountBucket(int count) => count switch
    {
        <= 0 => "0",
        1 => "1",
        <= 10 => "2_10",
        <= 50 => "11_50",
        <= 200 => "51_200",
        _ => "201_plus"
    };

    public static string NavigationResult(bool accepted) =>
        accepted ? Results.Success : Results.Rejected;

    public static string EnumValue<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        Regex.Replace(value.ToString(), "(?<=[a-z0-9])(?=[A-Z])", "_",
                RegexOptions.CultureInvariant)
            .ToLowerInvariant();

    // This catalog is the reviewable contract for identifier-free emitter dimensions.
    // It deliberately contains product categories only, never user or repository data.
    internal static IReadOnlyDictionary<string, IReadOnlyCollection<string>> EmitterValueCatalog { get; } =
        new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal)
        {
            ["result"] = Values(
                Results.AlreadyGranted, "allowed", Results.Authenticated, "auth_error", "cached_error", "cancelled", "deferred", "disabled", "dismissed",
                Results.Collapsed, "empty", Results.Enabled, "error", Results.Expanded, Results.Failed, "hidden", "launched", "opened", "partial",
                "no_session", Results.PermissionDenied, "preview", Results.Queued, "reconnect", "rejected", "retry", "shown",
                "staged", "started", "submitted", "success", "suppressed", "unavailable", "unknown", "visible",
                Results.IdentityError, Results.IdentityUnavailable, Results.NetworkError),
            ["cache_state"] = Values("cache_only", "cached", "fresh", "local", "miss", "network_only", "stale", "unknown"),
            ["page"] = Values(
                "authenticated", "auth", "code", "commits", "dashboard", "explore", "feedback", "gists",
                "home", "issues", "my", "notifications", "profile", "pull_requests", "repo", "repo_code",
                "repositories", "repository_search", "settings", "stars", "user"),
            ["section"] = Values(
                "about", "appearance",
                "activity", "assignees", "checks", "comments", "commits", "compare", "conversation", "diff", "files", "followers", "following",
                "data_cache", "diagnostics", "general", "identity", "inspector", "issues", "labels", "list", "metadata", "milestone", "notifications",
                "overview", "preview", "pull_requests", "readme", "recommendations", "repositories", "reviews", "stars",
                "privacy", "status", "timeline"),
            ["source"] = Values(
                Sources.Action, "background", Sources.Cache, "command", "command_search", Sources.Dialog, Sources.Dwell, "edit", "energy_saver", Sources.Full, "history", "home",
                "hover", Sources.Incremental, "initial", "list", "login", "manual", "memory_pressure", "metered_connection", "navigation", "none", "offline", "pagination", "preview",
                Sources.NavigationHandoff, Sources.Neighbor, Sources.Notifications, "profile", "profile_organization", "profile_selector", "query", "refresh", "repository_library",
                "rate_limit_headroom", "route", Sources.Scope, "session", "shell", "sign_in", "stars", Sources.Startup, Sources.User, "callback"),
            ["action"] = Values(
                Actions.Add, "my_issues", "my_pull_requests", "add_category", "assign_category", Actions.BreadcrumbPath,
                Actions.BreadcrumbRoot, "browse_files", "clear_all", Actions.ClearAllCache, "clear_all_filters", "clear_cache",
                Actions.ClearDiagnostics, "clear_filter", Actions.ClearImageCache, "clear_images", Actions.ClearQueryCache,
                Actions.ClearRepoFileCache, Actions.ClearStarsLibrary,
                Actions.Close, Actions.Comment, Actions.CommentDelete, Actions.CommentEdit, Actions.CommentHide, Actions.CommentPin,
                Actions.CommentReaction, Actions.CommentUnhide, Actions.CommentUnpin, Actions.CopyCode, Actions.CopyDiff, Actions.CopyFact, "copy_file", Actions.CopyLink, Actions.CopyMarkdown,
                Actions.CopySelection, "copy_sha", Actions.Create, Actions.CsvCopy, Actions.CsvPlainView, Actions.CsvReorder, Actions.CsvResize,
                Actions.CsvRichView, Actions.CsvSort, Actions.Delete, "detail_selection", Actions.CopyLineLink, Actions.CopyPath, Actions.CopyRaw,
                Actions.DeveloperMode, Actions.Diagnostics,
                Actions.Drawer, Actions.Edit, "edit_profile", "export", Actions.ExportDiagnostics, Actions.ExternalOpen,
                "filter_changed", Actions.Find, Actions.Follow,
                Actions.Hydrate, Actions.LoadRemoteImages, "load_full_file", "load_next_page", "manage_repositories", Actions.Merge, "new_repository",
                Actions.MarkAllRead, Actions.MarkRead, Actions.Metadata, Actions.Mute,
                "open", "open_activity_repository", "open_fact", "open_gists", "open_organization", "open_owner",
                "open_person", "open_repositories", Actions.OpenLink, Actions.OpenProfileExternal, Actions.OpenRepository, "open_repository_external", "open_source",
                "open_stars", Actions.Outline, "reaction", "refresh", Actions.RefreshUser, Actions.Remove, "remove_category",
                Actions.QuoteReply, Actions.Reaction, Actions.Render, Actions.Reopen, Actions.Reorder, "reset", Actions.Retry, Actions.ReviewApprove, Actions.ReviewComment,
                Actions.ReviewReply, Actions.ReviewRequestChanges, "search", "search_repositories",
                Actions.SectionChanged, Actions.SelectionMode, "share", Actions.SignIn, Actions.SignOut, "sort_changed", "scope_upgrade", Actions.StoreTelemetry,
                Actions.SyncStar, Actions.SyncUnstar, Actions.ThemeChanged, Actions.ToggleDetails, Actions.ToggleState, Actions.UndoUnstar, Actions.Unfollow, Actions.Unstar,
                Actions.Unmute, Actions.Unsubscribe, Actions.Update, "view_all", "write"),
            ["filter_type"] = Values(
                FilterTypes.All, "archive", "author", "branch", "category", "date", "fork", "language", "list", "owner", "path",
                FilterTypes.Participating, "activity", "kind", "navigation", "search", "sort", "state", "topic",
                FilterTypes.Unread, "visibility", "unknown"),
            ["view_mode"] = Values("dark", "light", "split", "system", "unified"),
            ["sort"] = Values("least_recently_active", "most_stars", "name", "recently_active", "recently_starred"),
            ["error_kind"] = Values(
                "access_denied", ErrorKinds.Api, ErrorKinds.Authentication, ErrorKinds.Cancelled, "invalid_operation", ErrorKinds.Io, ErrorKinds.Network,
                ErrorKinds.InvalidCallback, ErrorKinds.Launch, "permission", "rate_limit", "storage", "unexpected", "unknown"),
            ["phase"] = Values("background", "complete", "execute", "incremental", "initial", "schedule"),
            ["feature"] = Values("command_search", "commits", "issues", "markdown", "prefetch", "pull_requests", "repository_library"),
            ["priority"] = Values("background_refresh", "prefetch", "user_initiated", "visible"),
            ["query_kind"] = Values("immutable_sha", "mutable", "repo_metadata", "search"),
            ["resource"] = Values("avatar_image", "file", "gist_cache_index", "gist_raw_file", "link", "lookup", "mutable", "remote_image", "repositories", "repository", "search"),
            ["policy"] = Values("allowed", "deferred", "suppressed"),
            ["event_kind"] = Values("action", "navigation", "query"),
            ["status"] = Values("complete", "failed", "partial", "queued", "success", "unavailable"),
            ["widget"] = Values(
                "notifications", "overview", "quick_actions", "recent_activity", "recommended_repositories", "repositories")
        };

    internal static IReadOnlyCollection<string> AllRegisteredCategoryValues { get; } =
        EmitterValueCatalog.Values.SelectMany(static values => values).ToHashSet(StringComparer.Ordinal);

    private static IReadOnlyCollection<string> Values(params string[] values) =>
        Array.AsReadOnly(values);
}

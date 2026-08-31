using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JitHub.Services.CodeViewer;
using JitHub.Services;
using JitHub.Services.Markdown;
using JitHub.WinUI.Helpers;
using Windows.System;

namespace JitHub.WinUI.ViewModels.CodeViewer;

public sealed partial class RepoCodeBreadcrumbViewModel : ObservableObject
{
    public ObservableCollection<BreadcrumbSegment> Segments { get; } = [];

    [ObservableProperty]
    public partial string? CurrentRawUrl { get; set; }

    [ObservableProperty]
    public partial string? CurrentGitHubUrl { get; set; }

    [ObservableProperty]
    public partial bool IsCopyPathDone { get; set; }

    [ObservableProperty]
    public partial bool IsCopyRawUrlDone { get; set; }

    [ObservableProperty]
    public partial string CurrentFileName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CurrentPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsPathTransitioning { get; set; }

    /// <summary>
    /// Optional callback invoked when the user taps a breadcrumb segment.
    /// The page VM wires this to expand the tree to that folder.
    /// </summary>
    public Func<BreadcrumbSegment, System.Threading.CancellationToken, System.Threading.Tasks.Task>? OnNavigate { get; set; }

    public Action<string, string>? OnActionCompleted { get; set; }

    [RelayCommand]
    private async System.Threading.Tasks.Task NavigateToSegmentAsync(
        BreadcrumbSegment? segment,
        System.Threading.CancellationToken ct)
    {
        if (segment is not null && OnNavigate is not null)
        {
            await OnNavigate(segment, ct);
        }
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task CopyPathAsync(System.Threading.CancellationToken ct)
    {
        string? path = GetCurrentFilePath();
        if (path is null) return;

        bool succeeded = PlatformHelper.CopyString(path);
        CompleteAction(RepoCodeTelemetryActions.CopyPath, succeeded);
        if (!succeeded) return;

        IsCopyPathDone = true;
        try { await System.Threading.Tasks.Task.Delay(1500, ct); } catch (OperationCanceledException) { }
        finally { IsCopyPathDone = false; }
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task CopyRawUrlAsync(System.Threading.CancellationToken ct)
    {
        if (CurrentRawUrl is null) return;

        bool succeeded = PlatformHelper.CopyString(CurrentRawUrl);
        CompleteAction(RepoCodeTelemetryActions.CopyRaw, succeeded);
        if (!succeeded) return;

        IsCopyRawUrlDone = true;
        try { await System.Threading.Tasks.Task.Delay(1500, ct); } catch (OperationCanceledException) { }
        finally { IsCopyRawUrlDone = false; }
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task OpenOnGitHubAsync()
    {
        if (CurrentGitHubUrl is null ||
            !Uri.TryCreate(CurrentGitHubUrl, UriKind.Absolute, out Uri? uri) ||
            !MarkdownLinkNavigationPolicy.IsAllowedLaunchUri(uri))
        {
            CompleteAction(RepoCodeTelemetryActions.ExternalOpen, succeeded: false);
            return;
        }

        try
        {
            CompleteAction(
                RepoCodeTelemetryActions.ExternalOpen,
                await Launcher.LaunchUriAsync(uri));
        }
        catch (Exception)
        {
            CompleteAction(RepoCodeTelemetryActions.ExternalOpen, succeeded: false);
        }
    }

    private void CompleteAction(string action, bool succeeded) =>
        OnActionCompleted?.Invoke(
            action,
            succeeded ? TelemetryTaxonomy.Results.Success : TelemetryTaxonomy.Results.Error);

    /// <summary>
    /// Presents the selected path without rebuilding the interactive segments.
    /// </summary>
    internal void PrimePath(string repoName, string filePath)
    {
        CurrentPath = filePath;
        CurrentFileName = GetFileName(repoName, filePath);
        IsPathTransitioning = true;
    }

    /// <summary>
    /// Rebuilds segments from a file path (e.g. "src/foo/Bar.cs").
    /// The first segment is always the repo root with <paramref name="repoName"/> as label.
    /// </summary>
    internal void BuildFromPath(string repoName, string filePath)
    {
        Segments.Clear();
        Segments.Add(new BreadcrumbSegment(repoName, string.Empty, IsRoot: true));

        CurrentPath = filePath;
        CurrentFileName = GetFileName(repoName, filePath);

        if (string.IsNullOrEmpty(filePath))
        {
            IsPathTransitioning = false;
            return;
        }

        string[] parts = filePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string accumulated = string.Empty;
        foreach (string part in parts)
        {
            accumulated = accumulated.Length == 0 ? part : accumulated + "/" + part;
            Segments.Add(new BreadcrumbSegment(part, accumulated, IsRoot: false));
        }

        IsPathTransitioning = false;
    }

    private string? GetCurrentFilePath() =>
        string.IsNullOrEmpty(CurrentPath) ? null : CurrentPath;

    private static string GetFileName(string repoName, string filePath) =>
        string.IsNullOrEmpty(filePath)
            ? repoName
            : filePath.Split('/', StringSplitOptions.RemoveEmptyEntries)[^1];
}

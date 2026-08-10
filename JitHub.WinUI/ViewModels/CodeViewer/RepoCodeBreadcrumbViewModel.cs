using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JitHub.Services.CodeViewer;
using Windows.ApplicationModel.DataTransfer;
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

    /// <summary>
    /// Optional callback invoked when the user taps a breadcrumb segment.
    /// The page VM wires this to expand the tree to that folder.
    /// </summary>
    public Func<BreadcrumbSegment, System.Threading.CancellationToken, System.Threading.Tasks.Task>? OnNavigate { get; set; }

    public Action<string>? OnActionExecuted { get; set; }

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

        var dp = new DataPackage();
        dp.SetText(path);
        Clipboard.SetContent(dp);
        OnActionExecuted?.Invoke(RepoCodeTelemetryActions.CopyPath);

        IsCopyPathDone = true;
        try { await System.Threading.Tasks.Task.Delay(1500, ct); } catch (OperationCanceledException) { }
        finally { IsCopyPathDone = false; }
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task CopyRawUrlAsync(System.Threading.CancellationToken ct)
    {
        if (CurrentRawUrl is null) return;

        var dp = new DataPackage();
        dp.SetText(CurrentRawUrl);
        Clipboard.SetContent(dp);
        OnActionExecuted?.Invoke(RepoCodeTelemetryActions.CopyRaw);

        IsCopyRawUrlDone = true;
        try { await System.Threading.Tasks.Task.Delay(1500, ct); } catch (OperationCanceledException) { }
        finally { IsCopyRawUrlDone = false; }
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task OpenOnGitHubAsync()
    {
        if (CurrentGitHubUrl is not null && Uri.TryCreate(CurrentGitHubUrl, UriKind.Absolute, out Uri? uri))
        {
            bool launched = await Launcher.LaunchUriAsync(uri);
            if (launched)
            {
                OnActionExecuted?.Invoke(RepoCodeTelemetryActions.ExternalOpen);
            }
        }
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
        CurrentFileName = string.IsNullOrEmpty(filePath)
            ? repoName
            : filePath.Split('/', StringSplitOptions.RemoveEmptyEntries)[^1];

        if (string.IsNullOrEmpty(filePath)) return;

        string[] parts = filePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string accumulated = string.Empty;
        foreach (string part in parts)
        {
            accumulated = accumulated.Length == 0 ? part : accumulated + "/" + part;
            Segments.Add(new BreadcrumbSegment(part, accumulated, IsRoot: false));
        }
    }

    private string? GetCurrentFilePath()
    {
        // The last non-root segment is the current file/folder path.
        for (int i = Segments.Count - 1; i >= 0; i--)
        {
            if (!Segments[i].IsRoot) return Segments[i].Path;
        }
        return null;
    }
}

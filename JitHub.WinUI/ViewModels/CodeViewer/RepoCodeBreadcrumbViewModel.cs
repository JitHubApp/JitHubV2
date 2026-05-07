using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    /// <summary>
    /// Optional callback invoked when the user taps a breadcrumb segment.
    /// The page VM wires this to expand the tree to that folder.
    /// </summary>
    public Action<BreadcrumbSegment>? OnNavigate { get; set; }

    [RelayCommand]
    private async System.Threading.Tasks.Task NavigateToSegmentAsync(BreadcrumbSegment? segment)
    {
        if (segment is not null)
        {
            OnNavigate?.Invoke(segment);
        }
        await System.Threading.Tasks.Task.CompletedTask;
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task CopyPathAsync()
    {
        string? path = GetCurrentFilePath();
        if (path is null) return;

        var dp = new DataPackage();
        dp.SetText(path);
        Clipboard.SetContent(dp);
        await System.Threading.Tasks.Task.CompletedTask;
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task CopyRawUrlAsync()
    {
        if (CurrentRawUrl is null) return;

        var dp = new DataPackage();
        dp.SetText(CurrentRawUrl);
        Clipboard.SetContent(dp);
        await System.Threading.Tasks.Task.CompletedTask;
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task OpenOnGitHubAsync()
    {
        if (CurrentGitHubUrl is not null && Uri.TryCreate(CurrentGitHubUrl, UriKind.Absolute, out Uri? uri))
        {
            await Launcher.LaunchUriAsync(uri);
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

        if (string.IsNullOrEmpty(filePath)) return;

        string[] parts = filePath.Split('/');
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

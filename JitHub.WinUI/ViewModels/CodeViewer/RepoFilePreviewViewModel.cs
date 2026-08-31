using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JitHub.Models.CodeViewer;

namespace JitHub.WinUI.ViewModels.CodeViewer;

public sealed partial class RepoFilePreviewViewModel : ObservableObject
{
    [ObservableProperty]
    public partial RepoTreeNode? CurrentFile { get; set; }

    public string CurrentFilePath => CurrentFile?.Path ?? string.Empty;

    [ObservableProperty]
    public partial RepoFilePreviewKind Kind { get; set; }

    [ObservableProperty]
    public partial string LanguageId { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? Text { get; set; }

    [ObservableProperty]
    public partial byte[]? Bytes { get; set; }

    [ObservableProperty]
    public partial string? ImageMimeType { get; set; }

    [ObservableProperty]
    public partial long ByteSize { get; set; }

    [ObservableProperty]
    public partial string? Encoding { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial string? GitHubBlobUrl { get; set; }

    [ObservableProperty]
    public partial string? GitHubRawUrl { get; set; }

    [ObservableProperty]
    public partial bool ShowRichPreview { get; set; } = true;

    [RelayCommand]
    private void ToggleRichPreview() => ShowRichPreview = !ShowRichPreview;

    partial void OnCurrentFileChanged(RepoTreeNode? value) =>
        OnPropertyChanged(nameof(CurrentFilePath));

    internal void BeginSelection(RepoTreeNode file)
    {
        IsLoading = true;
        ErrorMessage = null;
        Kind = RepoFilePreviewKind.Code;
        LanguageId = string.Empty;
        Text = null;
        Bytes = null;
        ImageMimeType = null;
        ByteSize = 0;
        Encoding = null;
        GitHubBlobUrl = null;
        GitHubRawUrl = null;
        ShowRichPreview = true;
        CurrentFile = file;
    }

    internal void Reset()
    {
        CurrentFile = null;
        Kind = RepoFilePreviewKind.Code;
        LanguageId = string.Empty;
        Text = null;
        Bytes = null;
        ImageMimeType = null;
        ByteSize = 0;
        Encoding = null;
        IsLoading = false;
        ErrorMessage = null;
        GitHubBlobUrl = null;
        GitHubRawUrl = null;
        ShowRichPreview = true;
    }
}

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JitHub.Models.CodeViewer;

namespace JitHub.WinUI.ViewModels.CodeViewer;

public sealed partial class RepoFilePreviewViewModel : ObservableObject
{
    [ObservableProperty]
    public partial RepoTreeNode? CurrentFile { get; set; }

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
    public partial bool ShowRichPreview { get; set; } = true;

    [RelayCommand]
    private void ToggleRichPreview() => ShowRichPreview = !ShowRichPreview;

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
        ShowRichPreview = true;
    }
}

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JitHub.Models.CodeViewer;
using MarkdownRenderer.Images;

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

    private MarkdownViewerDocumentPayload? _richPreviewDocument;

    /// <summary>
    /// Atomically publishes the rich renderer's text and matching repository
    /// identity after the rest of this preview has been prepared.
    /// </summary>
    internal MarkdownViewerDocumentPayload? RichPreviewDocument
    {
        get => _richPreviewDocument;
        set => SetProperty(ref _richPreviewDocument, value);
    }

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
        RichPreviewDocument = null;
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
        RichPreviewDocument = null;
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

/// <summary>
/// One prepared Markdown render input. Text and its repository identity are
/// published together so the renderer never observes a mixed-document state.
/// </summary>
internal sealed record MarkdownViewerDocumentPayload(
    string Markdown,
    string? BaseUrl,
    string? DocumentPath,
    MarkdownDocumentSource? DocumentSource);

namespace MarkdownRenderer.Images;

/// <summary>
/// Stable identity and repository context for one logical Markdown document.
/// The document ID must change when a recycled host displays a different issue,
/// comment, pull request, commit, README, or editor preview.
/// </summary>
public sealed record MarkdownDocumentSource(
    string DocumentId,
    string? Owner = null,
    string? Repository = null,
    string? Ref = null,
    string? Path = null)
{
    /// <summary>Returns true only when complete repository-relative image context is available.</summary>
    public bool HasRepositoryContext =>
        !string.IsNullOrWhiteSpace(Owner) &&
        !string.IsNullOrWhiteSpace(Repository) &&
        !string.IsNullOrWhiteSpace(Ref) &&
        !string.IsNullOrWhiteSpace(Path);

    /// <summary>Returns a normalized identity suitable for per-document consent.</summary>
    public string GetConsentIdentity() => string.IsNullOrWhiteSpace(DocumentId)
        ? string.Empty
        : DocumentId.Trim();
}

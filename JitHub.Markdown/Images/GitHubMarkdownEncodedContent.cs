namespace JitHub.Markdown.Images;

/// <summary>
/// The encoded payload of a GitHub Contents or Blobs API response.
/// </summary>
/// <param name="Content">The encoded content, or <see langword="null"/> when GitHub omitted it.</param>
/// <param name="Encoding">The content encoding GitHub reported, such as <c>base64</c>.</param>
/// <param name="Sha">The Git object id, used to read a blob when a file's content is omitted.</param>
public sealed record GitHubMarkdownEncodedContent(string? Content, string? Encoding, string? Sha);

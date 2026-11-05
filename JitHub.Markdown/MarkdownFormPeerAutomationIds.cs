namespace JitHub.Markdown;

/// <summary>The automation ids of a Markdown form's interactive peers.</summary>
/// <param name="Mode">The Write/Preview mode selector.</param>
/// <param name="WriteMode">The Write mode item.</param>
/// <param name="PreviewMode">The Preview mode item.</param>
/// <param name="Editor">The Markdown text editor.</param>
/// <param name="Preview">The preview viewer.</param>
public readonly record struct MarkdownFormPeerAutomationIds(
    string Mode,
    string WriteMode,
    string PreviewMode,
    string Editor,
    string Preview);

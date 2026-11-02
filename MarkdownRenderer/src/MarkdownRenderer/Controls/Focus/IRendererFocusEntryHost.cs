namespace MarkdownRenderer.Controls.Focus;

/// <summary>What focus entry needs from the control.</summary>
internal interface IRendererFocusEntryHost
{
    /// <summary>Gives the control itself programmatic focus.</summary>
    bool FocusRendererProgrammatically();
}

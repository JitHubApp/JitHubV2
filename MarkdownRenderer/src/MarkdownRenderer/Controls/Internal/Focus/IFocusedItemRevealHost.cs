namespace MarkdownRenderer.Controls.Internal.Focus;

/// <summary>What revealing the focused item needs from the control.</summary>
internal interface IFocusedItemRevealHost
{
    /// <summary>Asks for focus geometry to be read again later.</summary>
    void RequestFocusGeometryRetry();
}

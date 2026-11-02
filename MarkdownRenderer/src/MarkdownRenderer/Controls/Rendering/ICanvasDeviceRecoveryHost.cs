namespace MarkdownRenderer.Controls.Rendering;

/// <summary>What device-loss recovery needs from the control.</summary>
internal interface ICanvasDeviceRecoveryHost
{
    /// <summary>Rebuilds the layout from the document.</summary>
    void RequestFullRebuild();

    /// <summary>Repaints the document canvas.</summary>
    void InvalidateCanvas();
}

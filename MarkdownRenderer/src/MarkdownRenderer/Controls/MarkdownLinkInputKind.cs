namespace MarkdownRenderer.Controls;

/// <summary>Identifies the input modality used to activate markdown content.</summary>
public enum MarkdownLinkInputKind
{
    /// <summary>The activation was raised by application code.</summary>
    Programmatic,
    /// <summary>Mouse input.</summary>
    Mouse,
    /// <summary>Touch input.</summary>
    Touch,
    /// <summary>Pen input.</summary>
    Pen,
    /// <summary>Keyboard input.</summary>
    Keyboard,
    /// <summary>A UI Automation client invoked the element.</summary>
    Automation,
}

namespace MarkdownRenderer.Mermaid.Internal.Rendering;

/// <summary>Describes the terminal state of a Mermaid render request.</summary>
internal enum MermaidRenderStatus
{
    Success,
    EngineUnavailable,
    InvalidInput,
    InvalidOptions,
    BudgetExceeded,
    Cancelled,
    TimedOut,
    UnsupportedLayout,
    InvalidScene,
    NativeFailure,
    Overloaded,
}

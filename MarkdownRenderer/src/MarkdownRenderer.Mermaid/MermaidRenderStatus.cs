using System.Globalization;

namespace MarkdownRenderer.Mermaid;

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

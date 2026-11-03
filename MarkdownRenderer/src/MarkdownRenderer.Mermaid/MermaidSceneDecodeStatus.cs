namespace MarkdownRenderer.Mermaid;

/// <summary>Stable failure categories returned when an untrusted MMIR buffer cannot be consumed.</summary>
internal enum MermaidSceneDecodeStatus
{
    Success,
    InvalidBudget,
    BufferTooSmall,
    BadMagic,
    UnsupportedVersion,
    InvalidHeader,
    SizeMismatch,
    BudgetExceeded,
    InvalidDirectory,
    DuplicateSection,
    OverlappingSections,
    MissingSection,
    InvalidSection,
    InvalidUtf8,
    InvalidReference,
    NonFiniteNumber,
    InvalidSourceMapping,
}

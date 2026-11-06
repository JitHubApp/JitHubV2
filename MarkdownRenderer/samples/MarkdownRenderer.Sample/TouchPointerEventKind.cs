namespace MarkdownRenderer.Sample;

/// <summary>
/// A routed pointer event that <see cref="TouchContactObservation"/> records.
/// The member names are part of the published observation JSON.
/// </summary>
internal enum TouchPointerEventKind
{
    None,
    Pressed,
    Moved,
    Released,
    Canceled,
    CaptureLost,
}

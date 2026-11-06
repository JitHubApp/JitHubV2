namespace MarkdownRenderer.Layout.Boxes;

// An accepted UI callback may remain queued after the source-read lease has
// drained. Carry only weak source identity and scalar/display metadata into
// that callback; never capture the resolver/provider byte array or a cache
// entry that owns it.
/// <summary>
/// The result of <see cref="SvgSourceOpener.OpenAsync"/>: opened render work,
/// a host-capacity deferral, or neither.
/// </summary>
internal readonly struct SvgOpenStageResult(
    SvgOpenedRenderWork? Work, SvgHostCapacityDeferral? Deferral)
{
    public SvgOpenedRenderWork? Work { get; init; } = Work;
    public SvgHostCapacityDeferral? Deferral { get; init; } = Deferral;
}

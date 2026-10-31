namespace MarkdownRenderer.Controls.Pipeline;

internal sealed class PipelineCancellationObservation(
    long Generation,
    long CancellationStartTimestamp)
{
    public readonly long Generation = Generation;
    public readonly long CancellationStartTimestamp = CancellationStartTimestamp;
}

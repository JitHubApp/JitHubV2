namespace MarkdownRenderer.Layout.Internal.Inlines;

internal readonly struct IntrinsicWidthMetrics
{
    public readonly float Minimum;
    public readonly float Preferred;

    public IntrinsicWidthMetrics(float Minimum, float Preferred)
    {
        this.Minimum = Minimum;
        this.Preferred = Preferred;
    }
}

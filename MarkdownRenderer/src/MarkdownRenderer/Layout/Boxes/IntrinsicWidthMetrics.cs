namespace MarkdownRenderer.Layout.Boxes;

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

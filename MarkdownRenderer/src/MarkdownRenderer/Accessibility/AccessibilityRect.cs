namespace MarkdownRenderer.Accessibility;

internal readonly struct AccessibilityRect
{
    public AccessibilityRect(double X, double Y, double Width, double Height)
    {
        this.X = X;
        this.Y = Y;
        this.Width = Width;
        this.Height = Height;
    }

    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
    public double Left => X;
    public double Top => Y;
    public double Right => X + Width;
    public double Bottom => Y + Height;
}

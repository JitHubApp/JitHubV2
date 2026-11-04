namespace MarkdownRenderer.Controls;

internal readonly struct TouchContactGeometry
{
    public TouchContactGeometry(double X, double Y, double Width, double Height)
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
    public double CenterX => X + Width / 2.0;
    public double CenterY => Y + Height / 2.0;
}

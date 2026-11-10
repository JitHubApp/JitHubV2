namespace MarkdownRenderer.Images.Svg;

/// <summary>An engine-neutral, non-premultiplied semantic color value.</summary>
/// <param name="Red">Red channel.</param>
/// <param name="Green">Green channel.</param>
/// <param name="Blue">Blue channel.</param>
/// <param name="Alpha">Alpha channel.</param>
public readonly record struct MarkdownSvgColor(
    byte Red,
    byte Green,
    byte Blue,
    byte Alpha = byte.MaxValue);

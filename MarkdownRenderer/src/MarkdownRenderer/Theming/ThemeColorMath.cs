using System;
using Windows.UI;

namespace MarkdownRenderer.Theming;

/// <summary>Color arithmetic shared by theme resolution: alpha, lightening and source-over compositing.</summary>
internal static class ThemeColorMath
{
    internal static Color WithAlpha(Color color, byte alpha) =>
        Color.FromArgb(alpha, color.R, color.G, color.B);

    internal static Color AdjustColor(Color color, float amount)
    {
        byte Adjust(byte channel)
        {
            float value = amount >= 0
                ? channel + (255 - channel) * amount
                : channel * (1 + amount);
            return (byte)Math.Clamp((int)Math.Round(value), 0, 255);
        }

        return Color.FromArgb(color.A, Adjust(color.R), Adjust(color.G), Adjust(color.B));
    }

    internal static Color CompositeOver(Color top, Color bottom)
    {
        double topA = top.A / 255.0;
        double bottomA = bottom.A / 255.0;
        double outA = topA + bottomA * (1.0 - topA);
        if (outA <= 0.0)
            return Color.FromArgb(0, 0, 0, 0);

        byte a = (byte)Math.Clamp((int)Math.Round(outA * 255.0), 0, 255);
        byte r = CompositeChannel(top.R, topA, bottom.R, bottomA, outA);
        byte g = CompositeChannel(top.G, topA, bottom.G, bottomA, outA);
        byte b = CompositeChannel(top.B, topA, bottom.B, bottomA, outA);
        return Color.FromArgb(a, r, g, b);
    }

    internal static byte CompositeChannel(byte top, double topA, byte bottom, double bottomA, double outA)
    {
        var value = (top * topA + bottom * bottomA * (1.0 - topA)) / outA;
        return (byte)Math.Clamp((int)Math.Round(value), 0, 255);
    }
}

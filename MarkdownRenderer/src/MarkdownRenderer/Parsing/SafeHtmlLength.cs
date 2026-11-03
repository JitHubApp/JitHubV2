using System;

namespace MarkdownRenderer.Parsing;

internal readonly struct SafeHtmlLength
{
    public SafeHtmlLength(float Value, bool IsPercent)
    {
        this.Value = Value;
        this.IsPercent = IsPercent;
    }

    public float Value { get; init; }
    public bool IsPercent { get; init; }

    public float Resolve(float available) => IsPercent
        ? Math.Max(0, available) * Math.Clamp(Value, 0, 100) / 100f
        : Math.Max(0, Value);
}

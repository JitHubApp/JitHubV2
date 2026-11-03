using System.Collections.Generic;
using System.Threading;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Accessibility;

internal readonly struct MarkdownTextStyleRun(
    int Start,
    int End,
    string ElementKey,
    bool IsSubscript,
    bool IsSuperscript,
    ElementStyle Style)
{
    internal int Start { get; init; } = Start;
    internal int End { get; init; } = End;
    internal readonly string ElementKey = ElementKey;
    internal readonly bool IsSubscript = IsSubscript;
    internal readonly bool IsSuperscript = IsSuperscript;
    internal readonly ElementStyle Style = Style;
}

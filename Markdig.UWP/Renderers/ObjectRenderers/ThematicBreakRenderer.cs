using Markdig.Syntax;
using Markdig.UWP.TextElements;
using System;

namespace Markdig.UWP.Renderers.ObjectRenderers;

internal class ThematicBreakRenderer : UWPObjectRenderer<ThematicBreakBlock>
{
    protected override void Write(UWPRenderer renderer, ThematicBreakBlock obj)
    {
        if (renderer == null) throw new ArgumentNullException(nameof(renderer));
        if (obj == null) throw new ArgumentNullException(nameof(obj));

        var thematicBreak = new MyThematicBreak(obj);

        renderer.WriteBlock(thematicBreak);
    }
}

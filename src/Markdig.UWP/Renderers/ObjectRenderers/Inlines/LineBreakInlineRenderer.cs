﻿using Markdig.Syntax.Inlines;
using Markdig.UWP.TextElements;
using System;

namespace Markdig.UWP.Renderers.ObjectRenderers.Inlines;

internal class LineBreakInlineRenderer : UWPObjectRenderer<LineBreakInline>
{
    protected override void Write(UWPRenderer renderer, LineBreakInline obj)
    {
        if (renderer == null) throw new ArgumentNullException(nameof(renderer));
        if (obj == null) throw new ArgumentNullException(nameof(obj));

        if (obj.IsHard)
        {
            renderer.WriteInline(new MyLineBreak());
        }
        else
        {
            // Soft line break.
            renderer.WriteText(" ");
        }
    }
}

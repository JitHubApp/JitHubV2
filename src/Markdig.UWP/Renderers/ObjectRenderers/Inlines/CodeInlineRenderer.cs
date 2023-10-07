using Markdig.Syntax.Inlines;
using Markdig.UWP.TextElements;
using System;

namespace Markdig.UWP.Renderers.ObjectRenderers.Inlines;

internal class CodeInlineRenderer : UWPObjectRenderer<CodeInline>
{
    protected override void Write(UWPRenderer renderer, CodeInline obj)
    {
        if (renderer == null) throw new ArgumentNullException(nameof(renderer));
        if (obj == null) throw new ArgumentNullException(nameof(obj));
        
        renderer.WriteInline(new MyInlineCode(obj));
    }
}

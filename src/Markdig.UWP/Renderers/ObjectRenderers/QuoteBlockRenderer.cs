using Markdig.Syntax;
using Markdig.UWP.TextElements;
using System;

namespace Markdig.UWP.Renderers.ObjectRenderers;

internal class QuoteBlockRenderer : UWPObjectRenderer<QuoteBlock>
{
    protected override void Write(UWPRenderer renderer, QuoteBlock obj)
    {
        if (renderer == null) throw new ArgumentNullException(nameof(renderer));
        if (obj == null) throw new ArgumentNullException(nameof(obj));

        var quote = new MyQuote(obj);

        renderer.Push(quote);
        renderer.WriteChildren(obj);
        renderer.Pop();
    }
}

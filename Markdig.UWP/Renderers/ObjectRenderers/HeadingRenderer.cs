using Markdig.Syntax;
using Markdig.UWP.TextElements;
using System;

namespace Markdig.UWP.Renderers.ObjectRenderers
{
    internal class HeadingRenderer : UWPObjectRenderer<HeadingBlock>
    {
        protected override void Write(UWPRenderer renderer, HeadingBlock obj)
        {
            if (renderer == null) throw new ArgumentNullException(nameof(renderer));
            if (obj == null) throw new ArgumentNullException(nameof(obj));

            var paragraph = new MyHeading(obj);
            renderer.Push(paragraph);
            renderer.WriteLeafInline(obj);
            renderer.Pop();
        }
    }
}

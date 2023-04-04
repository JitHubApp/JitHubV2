using Markdig.Syntax;
using Markdig.UWP.TextElements;

namespace Markdig.UWP.Renderers.ObjectRenderers
{
    internal class CodeBlockRenderer : UWPObjectRenderer<CodeBlock>
    {
        protected override void Write(UWPRenderer renderer, CodeBlock obj)
        {
            var code = new MyCodeBlock(obj);
            renderer.Push(code);
            renderer.Pop();
        }
    }
}

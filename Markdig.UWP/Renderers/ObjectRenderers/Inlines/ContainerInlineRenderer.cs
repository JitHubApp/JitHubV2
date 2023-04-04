using Markdig.Syntax.Inlines;

namespace Markdig.UWP.Renderers.ObjectRenderers.Inlines;

internal class ContainerInlineRenderer : UWPObjectRenderer<ContainerInline>
{
    protected override void Write(UWPRenderer renderer, ContainerInline obj)
    {
        foreach (var inline in obj)
        {
            renderer.Write(inline);
        }
    }
}

using Markdig.Renderers;
using Markdig.Syntax;

namespace Markdig.UWP.Renderers;

public abstract class UWPObjectRenderer<TObject> : MarkdownObjectRenderer<UWPRenderer, TObject>
    where TObject : MarkdownObject
{
}

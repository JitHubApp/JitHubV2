using Microsoft.UI.Xaml.Documents;

namespace Markdig.UWP.TextElements;

public interface IAddChild
{
    TextElement TextElement { get; }
    void AddChild(IAddChild child);
}

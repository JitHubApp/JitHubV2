using System.Windows.Input;

namespace MarkdownRenderer.Hosting;

/// <summary>Resolves host-owned commands for markdown targets.</summary>
public interface IMarkdownCommandProvider
{
    /// <summary>Returns a command for the supplied target, or null for library behavior.</summary>
    ICommand? GetCommand(MarkdownCommandContext context);
}

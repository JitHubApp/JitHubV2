using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using MarkdownRenderer.Images;
using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace MarkdownRenderer.Hosting;

/// <summary>Resolves host-owned commands for markdown targets.</summary>
public interface IMarkdownCommandProvider
{
    /// <summary>Returns a command for the supplied target, or null for library behavior.</summary>
    ICommand? GetCommand(MarkdownCommandContext context);
}

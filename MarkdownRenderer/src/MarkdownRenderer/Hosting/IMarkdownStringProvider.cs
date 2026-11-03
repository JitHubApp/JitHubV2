using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Input;
using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Images;
using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace MarkdownRenderer.Hosting;

/// <summary>Supplies localized renderer strings without exposing resource internals.</summary>
public interface IMarkdownStringProvider
{
    /// <summary>
    /// Returns a localized value for a stable renderer key, or null to use the
    /// library fallback. Implementations must be thread-safe.
    /// </summary>
    string? GetString(string key, CultureInfo culture);
}

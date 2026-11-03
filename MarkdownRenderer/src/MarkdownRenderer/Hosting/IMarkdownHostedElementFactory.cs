using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Images;
using Microsoft.UI.Xaml;

namespace MarkdownRenderer.Hosting;

/// <summary>
/// Creates WinUI elements only for declarative hosted-content requests that
/// are near the effective viewport.
/// </summary>
public interface IMarkdownHostedElementFactory
{
    /// <summary>Creates a hosted element on the UI thread.</summary>
    ValueTask<FrameworkElement?> CreateAsync(
        MarkdownHostedElementRequest request,
        CancellationToken cancellationToken);

    /// <summary>Releases host-owned state when an element is virtualized away.</summary>
    void Recycle(MarkdownHostedElementRequest request, FrameworkElement element)
    {
    }
}

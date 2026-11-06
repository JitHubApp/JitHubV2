using System.Threading;
using MarkdownRenderer.Images;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace MarkdownRenderer.Controls.Pipeline;

/// <summary>What the rebuild setup stage needs from the control.</summary>
internal interface IRebuildLayoutSetupHost
{
    DispatcherQueue? DispatcherQueue { get; }

    /// <summary>The element whose resources the theme resolves against.</summary>
    FrameworkElement ThemeHost { get; }

    /// <summary>The identity that owns the performance session's document scope.</summary>
    object DocumentOwner { get; }

    /// <summary>The loaded lifetime of image resolution, if loaded.</summary>
    CancellationTokenSource? ImageLifetimeCts { get; }

    void RaiseImageUnavailable(
        string source,
        MarkdownImageUnavailableReason reason,
        MarkdownSvgFailureReason? svgFailureReason);
}

using MarkdownRenderer.ScrollFidelityProbe.Hosting;
using Microsoft.UI.Xaml.Controls;

namespace MarkdownRenderer.ScrollFidelityProbe.Scenarios;

/// <summary>What a scenario drives: the fresh renderer's scroll viewer and the window it sits in.</summary>
internal sealed record ScenarioContext(
    ProbeRendererHost Host,
    nint Window,
    double RasterizationScale,
    CancellationToken Cancellation)
{
    internal ScrollViewer ScrollViewer => Host.ScrollViewer;

    internal double MaximumOffset => Math.Max(0, ScrollViewer.ExtentHeight - ScrollViewer.ViewportHeight);
}

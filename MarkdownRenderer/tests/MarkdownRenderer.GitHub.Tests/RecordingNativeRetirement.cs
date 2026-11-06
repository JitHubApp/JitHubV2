using MarkdownRenderer.Layout;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// A synchronous native retirement owner that records every active layer and
/// clip geometry it is asked to dispose, then disposes it.
/// </summary>
internal sealed class RecordingNativeRetirement
{
    internal RecordingNativeRetirement()
    {
        Retirement = new NativeResourceRetirement(createTimer: false, resource =>
        {
            if (resource is CanvasActiveLayer layer)
                LayerDisposalAttempts.Add(layer);
            if (resource is CanvasGeometry geometry)
                GeometryDisposalAttempts.Add(geometry);
            resource.Dispose();
        });
    }

    internal NativeResourceRetirement Retirement { get; }

    internal List<CanvasActiveLayer> LayerDisposalAttempts { get; } = [];

    internal List<CanvasGeometry> GeometryDisposalAttempts { get; } = [];
}

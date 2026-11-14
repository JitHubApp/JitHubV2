using System;
using Microsoft.UI.Xaml;
using RebuildReason = MarkdownRenderer.Controls.Internal.Pipeline.Rebuild.MarkdownRebuildCoordinatorReason;
using MarkdownRenderer.Controls.Internal.Pipeline.Rebuild;

namespace MarkdownRenderer.Controls.Internal.Pipeline;

/// <summary>
/// Restyles the loaded view when its width moves away from the width the
/// last rebuild laid out at.
/// </summary>
internal sealed class LayoutWidthWatch
{
    private readonly FrameworkElement _renderer;
    private readonly RebuildScheduler _rebuild;
    private float _lastWidth;
    private SizeChangedEventHandler? _sizeChangedHandler;

    internal LayoutWidthWatch(FrameworkElement renderer, RebuildScheduler rebuild)
    {
        _renderer = renderer;
        _rebuild = rebuild;
    }

    /// <summary>Records the width a rebuild lays out at.</summary>
    internal void Record(float width) => _lastWidth = width;

    internal void Attach()
    {
        _sizeChangedHandler = (_, e) =>
        {
            if (Math.Abs(_lastWidth - (float)e.NewSize.Width) > 0.5f) _rebuild.Request(RebuildReason.Restyle);
        };
        _renderer.SizeChanged += _sizeChangedHandler;
    }

    internal void Detach()
    {
        if (_sizeChangedHandler is not null)
        {
            _renderer.SizeChanged -= _sizeChangedHandler;
            _sizeChangedHandler = null;
        }
    }
}

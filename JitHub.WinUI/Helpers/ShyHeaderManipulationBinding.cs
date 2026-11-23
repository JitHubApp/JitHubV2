using System;
using JitHub.Core.Services.Layout;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.WinUI.Helpers;

/// <summary>
/// Tells a shy header tracker when a touch or touchpad manipulation is holding
/// a scroll viewer, for as long as the binding is alive.
/// </summary>
internal sealed partial class ShyHeaderManipulationBinding : IDisposable
{
    private readonly ScrollViewer _scrollViewer;
    private readonly ShyHeaderScrollTracker _tracker;
    private readonly Action _manipulationCompleted;
    private bool _isManipulating;
    private bool _isDisposed;

    public ShyHeaderManipulationBinding(
        ScrollViewer scrollViewer,
        ShyHeaderScrollTracker tracker,
        Action manipulationCompleted)
    {
        _scrollViewer = scrollViewer;
        _tracker = tracker;
        _manipulationCompleted = manipulationCompleted;
        _scrollViewer.DirectManipulationStarted += ScrollViewer_DirectManipulationStarted;
        _scrollViewer.DirectManipulationCompleted += ScrollViewer_DirectManipulationCompleted;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _scrollViewer.DirectManipulationStarted -= ScrollViewer_DirectManipulationStarted;
        _scrollViewer.DirectManipulationCompleted -= ScrollViewer_DirectManipulationCompleted;
        EndManipulation();
    }

    private void ScrollViewer_DirectManipulationStarted(object? sender, object e)
    {
        _isManipulating = true;
        _tracker.BeginManipulation();
    }

    private void ScrollViewer_DirectManipulationCompleted(object? sender, object e)
    {
        EndManipulation();
        _manipulationCompleted();
    }

    private void EndManipulation()
    {
        if (_isManipulating)
        {
            _isManipulating = false;
            _tracker.EndManipulation();
        }
    }
}

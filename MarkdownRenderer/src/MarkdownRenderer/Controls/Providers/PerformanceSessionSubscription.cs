using System;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Controls.Pipeline;
using MarkdownRenderer.Controls.Selection;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Selection;
using Microsoft.UI.Xaml;
using RebuildReason = MarkdownRenderer.Controls.MarkdownRebuildCoordinatorReason;

namespace MarkdownRenderer.Controls.Providers;

/// <summary>
/// The control's tie to its borrowed performance session: validates the
/// assigned session, releases the control's document scope from a replaced
/// session, and rebuilds when the loaded view's session is disposed so the
/// eager engine/legacy parser path replaces any committed fallback.
/// </summary>
/// <remarks>
/// The session raises Disposed on any thread. The subscribed instance is kept
/// outside DependencyObject storage so that callback never reads a
/// thread-affine dependency property.
/// </remarks>
internal sealed class PerformanceSessionSubscription
{
    private readonly MarkdownRendererControl _control;
    private readonly MarkdownViewLifetime _lifetime;
    private readonly SnapshotRetirementTracker _snapshotRetirements;
    private readonly RebuildScheduler _rebuild;
    private readonly SelectionController _selection;
    private readonly SelectionAdornerPresenter _adorner;
    private readonly InteractiveTextAdorner _textAdorner;
    private IMarkdownPerformanceSessionInternal? _subscribed;

    internal PerformanceSessionSubscription(
        MarkdownRendererControl control,
        MarkdownViewLifetime lifetime,
        SnapshotRetirementTracker snapshotRetirements,
        RebuildScheduler rebuild,
        SelectionController selection,
        SelectionAdornerPresenter adorner,
        InteractiveTextAdorner textAdorner)
    {
        _control = control;
        _lifetime = lifetime;
        _snapshotRetirements = snapshotRetirements;
        _rebuild = rebuild;
        _selection = selection;
        _adorner = adorner;
        _textAdorner = textAdorner;
    }

    /// <summary>The PerformanceSession property changed.</summary>
    internal void OnChanged(DependencyPropertyChangedEventArgs args)
    {
        if (args.NewValue is not null and not IMarkdownPerformanceSessionInternal)
        {
            _control.SetValue(MarkdownRendererControl.PerformanceSessionProperty, args.OldValue);
            throw new ArgumentException(
                "The performance session must be supplied by the optional MarkdownRenderer.Performance package.",
                nameof(MarkdownRendererControl.PerformanceSession));
        }
        Detach();
        if (args.OldValue is IMarkdownPerformanceSessionInternal oldSession)
        {
            _snapshotRetirements.Track(oldSession.ReleaseDeferredSceneDocument(_control));
            oldSession.ReleaseDocumentScope(_control);
        }
        Attach();
        _rebuild.Request(RebuildReason.Full);
        if (_selection.IsActive)
            _textAdorner.UpdateSelectionOverlay();
        else if (_textAdorner.HasContent())
            _adorner.InvalidateGeometry();
    }

    /// <summary>Subscribes to the current session's disposal while the view is loaded.</summary>
    internal void Attach()
    {
        if (_lifetime.IsDisposed || _lifetime.IsUnloaded ||
            _control.PerformanceSession is not IMarkdownPerformanceSessionInternal session)
        {
            return;
        }

        session.Disposed -= OnDisposed;
        Volatile.Write(ref _subscribed, session);
        session.Disposed += OnDisposed;
    }

    internal void Detach()
    {
        IMarkdownPerformanceSessionInternal? previous =
            Interlocked.Exchange(ref _subscribed, null);
        if (previous is not null)
            previous.Disposed -= OnDisposed;
    }

    /// <summary>Advances the session's deferred scene generation for a new pipeline, tracking its retirement.</summary>
    internal void AdvanceDeferredSceneGeneration()
    {
        if (_control.PerformanceSession is IMarkdownPerformanceSessionInternal performanceSession)
        {
            Task sceneRetirement = performanceSession.AdvanceDeferredSceneGeneration(_control);
            _snapshotRetirements.Track(sceneRetirement);
        }
    }

    /// <summary>Releases the control's deferred scenes and document scope from the current session, at unload.</summary>
    internal void ReleaseDocumentScope()
    {
        if (_control.PerformanceSession is IMarkdownPerformanceSessionInternal performanceSession)
        {
            _snapshotRetirements.Track(performanceSession.ReleaseDeferredSceneDocument(_control));
            performanceSession.ReleaseDocumentScope(_control);
        }
    }

    private void OnDisposed(object? sender, EventArgs args)
    {
        if (!ReferenceEquals(sender, Volatile.Read(ref _subscribed)) ||
            _lifetime.IsDisposed ||
            _lifetime.IsUnloaded)
        {
            return;
        }

        _control.DispatcherQueue?.TryEnqueue(() =>
        {
            if (ReferenceEquals(sender, Volatile.Read(ref _subscribed)) &&
                !_lifetime.IsDisposed && !_lifetime.IsUnloaded)
            {
                // RebuildInternalAsync detects the disposed borrowed session
                // and uses the eager engine/legacy parser path, replacing any
                // committed code fallback with its native scene.
                _rebuild.Request(RebuildReason.Full);
            }
        });
    }
}

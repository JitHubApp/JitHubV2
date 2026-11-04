using MarkdownRenderer.Controls.Highlighting;
using MarkdownRenderer.Controls.Rendering;
using MarkdownRenderer.Performance;

namespace MarkdownRenderer.Controls;

/// <summary>
/// Schedules the viewport-driven background work of the published snapshot:
/// code-block syntax highlighting and, with a performance session that
/// defers offscreen scenes, deferred scene materialization. Nothing is
/// scheduled for a snapshot whose geometry needs recovery.
/// </summary>
internal sealed class VisibleWorkScheduler
{
    private readonly MarkdownRendererControl _control;
    private readonly PublishedLayout _published;
    private readonly CodeBlockHighlighting _highlighting;
    private readonly GeometryRecovery _geometryRecovery;

    internal VisibleWorkScheduler(
        MarkdownRendererControl control,
        PublishedLayout published,
        CodeBlockHighlighting highlighting,
        GeometryRecovery geometryRecovery)
    {
        _control = control;
        _published = published;
        _highlighting = highlighting;
        _geometryRecovery = geometryRecovery;
    }

    internal void ScheduleCodeBlockHighlighting()
    {
        if (_geometryRecovery.RequestIfRequired(_published.Snapshot))
            return;
        ScheduleDeferredScenes();
        _highlighting.ScheduleVisible();
    }

    internal void ScheduleDeferredScenes()
    {
        if (_geometryRecovery.RequestIfRequired(_published.Snapshot))
            return;
        IMarkdownPerformanceSessionInternal? session =
            _control.PerformanceSession as IMarkdownPerformanceSessionInternal;
        if (session is not null && !session.IsDisposed && session.Options.DeferOffscreenScenes)
            session.ScheduleDeferredScenes(_control);
    }
}

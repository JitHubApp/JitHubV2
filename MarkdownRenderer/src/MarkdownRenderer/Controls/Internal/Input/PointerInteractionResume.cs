using MarkdownRenderer.Controls.Internal.Images;
using MarkdownRenderer.Controls.Internal.Pipeline.Rebuild;
using MarkdownRenderer.Controls.Internal.View;

namespace MarkdownRenderer.Controls.Internal.Input;

/// <summary>
/// Resumes the rebuild and image relayout that a pointer interaction held
/// back, once the interaction ends.
/// </summary>
internal sealed class PointerInteractionResume
{
    private readonly MarkdownViewLifetime _lifetime;
    private readonly RebuildScheduler _rebuild;
    private readonly ImageRelayout _imageRelayout;

    internal PointerInteractionResume(MarkdownViewLifetime lifetime, RebuildScheduler rebuild, ImageRelayout imageRelayout)
    {
        _lifetime = lifetime;
        _rebuild = rebuild;
        _imageRelayout = imageRelayout;
    }

    internal void ResumePendingWork()
    {
        _rebuild.QueuePendingIfIdle();
        if (!_lifetime.IsDisposed && !_lifetime.IsUnloaded)
            _imageRelayout.QueueFirstPending();
    }
}

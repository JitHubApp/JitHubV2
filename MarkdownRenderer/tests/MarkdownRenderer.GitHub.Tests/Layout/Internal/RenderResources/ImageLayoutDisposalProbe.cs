using Microsoft.Graphics.Canvas.Text;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.RenderResources;

/// <summary>
/// A native retirement disposer for image text layouts that records each
/// layout weakly and can fail the next disposal until disposal is allowed.
/// </summary>
internal sealed class ImageLayoutDisposalProbe
{
    private readonly List<WeakReference<CanvasTextLayout>> _observed = [];
    private bool _failNext;
    private bool _allowDisposal;

    internal WeakReference<CanvasTextLayout>[] ObservedLayouts => _observed.ToArray();
    internal void FailNextDisposal() => _failNext = true;
    internal void AllowDisposal() => _allowDisposal = true;

    internal void Dispose(IDisposable resource)
    {
        CanvasTextLayout layout = Assert.IsType<CanvasTextLayout>(resource);
        _observed.Add(new WeakReference<CanvasTextLayout>(layout));
        if (!_allowDisposal && _failNext)
        {
            _failNext = false;
            throw new InvalidOperationException("Injected native image-layout disposal failure.");
        }
        layout.Dispose();
    }
}

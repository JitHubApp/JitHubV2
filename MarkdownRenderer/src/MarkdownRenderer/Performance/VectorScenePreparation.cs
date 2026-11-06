using System;
using System.Threading;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;

namespace MarkdownRenderer.Performance;

/// <summary>
/// Optional-package recipe for staging a vector drawing without retaining its
/// owner. Capture only while the owning snapshot excludes disposal/mutation;
/// this method deliberately takes no lock and stores no box reference.
/// </summary>
internal readonly struct VectorScenePreparation
{
    private readonly ICanvasResourceCreator _resourceCreator;
    private readonly string? _hostFontFamily;
    private readonly VectorNativeResourceAdmission? _nativeAdmission;
    private readonly FlowDirection _fallbackFlowDirection;
    private readonly MarkdownLayoutDiagnostics? _layoutDiagnostics;
    private readonly MarkdownRenderResources _renderResources;

    private VectorScenePreparation(
        ICanvasResourceCreator resourceCreator,
        string? hostFontFamily,
        VectorNativeResourceAdmission? nativeAdmission,
        FlowDirection fallbackFlowDirection,
        MarkdownLayoutDiagnostics? layoutDiagnostics,
        MarkdownRenderResources renderResources)
    {
        _resourceCreator = resourceCreator;
        _hostFontFamily = hostFontFamily;
        _nativeAdmission = nativeAdmission;
        _fallbackFlowDirection = fallbackFlowDirection;
        _layoutDiagnostics = layoutDiagnostics;
        _renderResources = renderResources;
    }

    internal static VectorScenePreparation Capture(VectorSceneBox box)
    {
        ArgumentNullException.ThrowIfNull(box);
        MarkdownLayoutContext context = box.LayoutContext;
        return new VectorScenePreparation(
            context.ResourceCreator,
            box.ResolvedStyle.FontFamily,
            box.NativeAdmission,
            context.FlowDirection,
            context.LayoutDiagnostics,
            context.RenderResources);
    }

    internal VectorSceneDrawing Create(
        MarkdownVectorScene scene,
        CancellationToken cancellationToken,
        bool deferNativeResources = false) =>
        new(
            _resourceCreator,
            scene,
            cancellationToken,
            _renderResources,
            _hostFontFamily
            , _layoutDiagnostics
            , deferNativeResources, nativeAdmission: _nativeAdmission);

    /// <summary>Prewarms bounded fallback outside the snapshot/plan swap gates.</summary>
    internal bool PrepareCapacityFallback(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _nativeAdmission is not null &&
            _nativeAdmission.CapacityFallbackFormats.PrepareFormat(
                _renderResources.TextFormats,
                _fallbackFlowDirection);
    }
}

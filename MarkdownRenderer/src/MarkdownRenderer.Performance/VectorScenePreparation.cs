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
#if MARKDOWN_LAYOUT_DIAGNOSTICS
    private readonly MarkdownLayoutDiagnostics? _layoutDiagnostics;
#endif

    private VectorScenePreparation(
        ICanvasResourceCreator resourceCreator,
        string? hostFontFamily,
        VectorNativeResourceAdmission? nativeAdmission,
        FlowDirection fallbackFlowDirection
#if MARKDOWN_LAYOUT_DIAGNOSTICS
        , MarkdownLayoutDiagnostics? layoutDiagnostics
#endif
        )
    {
        _resourceCreator = resourceCreator;
        _hostFontFamily = hostFontFamily;
        _nativeAdmission = nativeAdmission;
        _fallbackFlowDirection = fallbackFlowDirection;
#if MARKDOWN_LAYOUT_DIAGNOSTICS
        _layoutDiagnostics = layoutDiagnostics;
#endif
    }

    internal static VectorScenePreparation Capture(VectorSceneBox box)
    {
        ArgumentNullException.ThrowIfNull(box);
        MarkdownLayoutContext context = box.LayoutContext;
        return new VectorScenePreparation(
            context.ResourceCreator,
            box.ResolvedStyle.FontFamily,
            box.NativeAdmission,
            context.FlowDirection
#if MARKDOWN_LAYOUT_DIAGNOSTICS
            , context.LayoutDiagnostics
#endif
            );
    }

    internal VectorSceneDrawing Create(
        MarkdownVectorScene scene,
        CancellationToken cancellationToken,
        bool deferNativeResources = false) =>
        new(
            _resourceCreator,
            scene,
            cancellationToken,
            _hostFontFamily
#if MARKDOWN_LAYOUT_DIAGNOSTICS
            , _layoutDiagnostics
#endif
            , deferNativeResources, nativeAdmission: _nativeAdmission);

    /// <summary>Prewarms bounded fallback outside the snapshot/plan swap gates.</summary>
    internal bool PrepareCapacityFallback(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _nativeAdmission is not null &&
            VectorSceneCapacityFallback.PrepareFormat(_fallbackFlowDirection);
    }
}

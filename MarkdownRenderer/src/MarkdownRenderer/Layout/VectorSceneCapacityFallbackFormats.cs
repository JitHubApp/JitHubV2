using System.Threading;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.UI.Text;

namespace MarkdownRenderer.Layout;

/// <summary>
/// The two text-direction descriptors that capacity-deferred vector scenes
/// paint their bounded text fallback with. A <see cref="VectorNativeResourceAdmission"/>
/// owns one set, because only scenes that admission deferred paint with it.
/// Individual scenes retain bounded strings, never a CanvasTextLayout.
/// </summary>
/// <remarks>
/// Painting borrows <see cref="CanvasTextFormat"/> directly, without taking a
/// lease, so a prepared descriptor is pinned for this owner's lifetime and its
/// lease is deliberately never released.
/// </remarks>
internal sealed class VectorSceneCapacityFallbackFormats
{
    private readonly object _gate = new();
    private SharedCanvasTextFormatCache.Lease? _leftToRightFormat;
    private SharedCanvasTextFormatCache.Lease? _rightToLeftFormat;

    /// <summary>
    /// Creates/pins the fixed format only from an off-frame preparation path.
    /// The returned descriptor remains owned by the shared cache lease, never
    /// by a scene/run, and is borrowed directly during painting.
    /// </summary>
    internal bool PrepareFormat(FlowDirection flowDirection)
    {
        lock (_gate)
        {
            ref SharedCanvasTextFormatCache.Lease? slot = ref
                (flowDirection == FlowDirection.RightToLeft
                    ? ref _rightToLeftFormat
                    : ref _leftToRightFormat);
            if (slot is not null)
                return true;

            var style = new ElementStyle
            {
                FontFamily = "Segoe UI",
                FontSize = 14f,
                LineHeightMultiplier = 1f,
                FontStyle = FontStyle.Normal,
            };
            SharedCanvasTextFormatCache.Lease lease = SharedCanvasTextFormatCache.Acquire(
                style,
                CanvasWordWrapping.Wrap,
                CanvasHorizontalAlignment.Center,
                flowDirection,
                localeName: null,
                useContentAwareLineSpacing: true,
                verticalAlignment: CanvasVerticalAlignment.Center);
            Volatile.Write(ref slot, lease);
            return true;
        }
    }

    internal bool TryGetFormat(FlowDirection flowDirection, out CanvasTextFormat format)
    {
        SharedCanvasTextFormatCache.Lease? lease = flowDirection == FlowDirection.RightToLeft
            ? Volatile.Read(ref _rightToLeftFormat)
            : Volatile.Read(ref _leftToRightFormat);
        if (lease is null)
        {
            format = null!;
            return false;
        }

        format = lease.Format;
        return true;
    }
}

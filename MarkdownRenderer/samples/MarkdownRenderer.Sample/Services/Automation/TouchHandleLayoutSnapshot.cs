using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using MarkdownRenderer.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace MarkdownRenderer.Sample.Services.Automation;

/// <summary>
/// Captures, as JSON, the layout of the renderer's touch selection handles so
/// the touch UI test can check their size, placement and hit-testing after a
/// contact boundary. It only reads the visual tree.
/// </summary>
internal static class TouchHandleLayoutSnapshot
{
    internal const string StartHandleAutomationId = "MarkdownSelectionStartHandle";
    internal const string EndHandleAutomationId = "MarkdownSelectionEndHandle";
    private const int MaximumVisualNodesToInspect = 4096;

    internal static string Capture(MarkdownRendererControl renderer, long generation)
    {
        Dictionary<string, FrameworkElement> handles = FindSelectionHandles(
            renderer,
            out int inspectedVisualNodes,
            out bool visualTreeSearchTruncated);
        XamlRoot? xamlRoot = renderer.XamlRoot;
        if (xamlRoot is null ||
            !double.IsFinite(xamlRoot.RasterizationScale) ||
            xamlRoot.RasterizationScale <= 0)
        {
            return "{\"status\":\"unavailable\",\"reason\":\"missingXamlRootOrScale\",\"rendererGeneration\":" +
                generation.ToString(CultureInfo.InvariantCulture) + "}";
        }

        double rasterizationScale = xamlRoot.RasterizationScale;
        var json = new StringBuilder(768);
        json.Append("{\"status\":\"captured\",\"rendererGeneration\":")
            .Append(generation.ToString(CultureInfo.InvariantCulture))
            .Append(",\"rasterizationScale\":");
        TouchContactJson.AppendNumber(json, rasterizationScale);
        AppendRendererLayout(json, renderer);
        json.Append(",\"inspectedVisualNodes\":")
            .Append(inspectedVisualNodes.ToString(CultureInfo.InvariantCulture))
            .Append(",\"visualTreeSearchTruncated\":")
            .Append(visualTreeSearchTruncated ? "true" : "false")
            .Append(",\"start\":");
        if (handles.TryGetValue(StartHandleAutomationId, out FrameworkElement? start))
            AppendHandleLayout(json, start, renderer, rasterizationScale);
        else
            json.Append("null");

        json.Append(",\"end\":");
        if (handles.TryGetValue(EndHandleAutomationId, out FrameworkElement? end))
            AppendHandleLayout(json, end, renderer, rasterizationScale);
        else
            json.Append("null");
        json.Append('}');
        return json.ToString();
    }

    private static void AppendRendererLayout(StringBuilder json, MarkdownRendererControl renderer)
    {
        json.Append(",\"rendererActualWidthDip\":");
        TouchContactJson.AppendNumber(json, renderer.ActualWidth);
        json.Append(",\"rendererActualHeightDip\":");
        TouchContactJson.AppendNumber(json, renderer.ActualHeight);
        json.Append(",\"snapshotSizeDip\":");
        if (renderer.RenderedContentSize is { } size)
        {
            json.Append("{\"width\":");
            TouchContactJson.AppendNumber(json, size.Width);
            json.Append(",\"height\":");
            TouchContactJson.AppendNumber(json, size.Height);
            json.Append('}');
        }
        else
        {
            json.Append("null");
        }
        // The renderer exposes the UI Automation scroll pattern only when it
        // owns its scroll viewport; with an ancestor ScrollViewer this is null.
        json.Append(",\"ownedScroll\":");
        if (FrameworkElementAutomationPeer.CreatePeerForElement(renderer)?.GetPattern(PatternInterface.Scroll)
            is IScrollProvider scroll)
        {
            json.Append("{\"verticallyScrollable\":")
                .Append(scroll.VerticallyScrollable ? "true" : "false")
                .Append(",\"verticalPercent\":");
            TouchContactJson.AppendNumber(json, scroll.VerticalScrollPercent);
            json.Append(",\"verticalViewSizePercent\":");
            TouchContactJson.AppendNumber(json, scroll.VerticalViewSize);
            json.Append('}');
        }
        else
        {
            json.Append("null");
        }
        json.Append(",\"rendererUseLayoutRounding\":")
            .Append(renderer.UseLayoutRounding ? "true" : "false");
    }

    private static Dictionary<string, FrameworkElement> FindSelectionHandles(
        MarkdownRendererControl renderer,
        out int inspectedVisualNodes,
        out bool truncated)
    {
        var handles = new Dictionary<string, FrameworkElement>(StringComparer.Ordinal);
        var pending = new Stack<DependencyObject>();
        pending.Push(renderer);
        inspectedVisualNodes = 0;

        while (pending.Count > 0 && handles.Count < 2 && inspectedVisualNodes < MaximumVisualNodesToInspect)
        {
            DependencyObject node = pending.Pop();
            inspectedVisualNodes++;
            if (node is FrameworkElement element)
            {
                string automationId = AutomationProperties.GetAutomationId(element);
                if (automationId is StartHandleAutomationId or EndHandleAutomationId)
                    handles.TryAdd(automationId, element);
            }

            int childCount = VisualTreeHelper.GetChildrenCount(node);
            for (int childIndex = childCount - 1; childIndex >= 0; childIndex--)
                pending.Push(VisualTreeHelper.GetChild(node, childIndex));
        }

        truncated = pending.Count > 0 && handles.Count < 2;
        return handles;
    }

    private static void AppendHandleLayout(
        StringBuilder json,
        FrameworkElement handle,
        MarkdownRendererControl renderer,
        double rasterizationScale)
    {
        AppendHandleSize(json, handle, rasterizationScale);
        AppendHandlePlacement(json, handle, renderer);
        json.Append(",\"peerBoundsRaw\":");
        AppendPeerBounds(json, handle);
        json.Append(",\"nearestCanvas\":");
        AppendNearestCanvas(json, handle);
        json.Append('}');
    }

    private static void AppendHandleSize(StringBuilder json, FrameworkElement handle, double rasterizationScale)
    {
        json.Append("{\"automationId\":");
        TouchContactJson.AppendString(json, AutomationProperties.GetAutomationId(handle));
        json.Append(",\"visibility\":");
        TouchContactJson.AppendString(json, handle.Visibility.ToString());
        json.Append(",\"isHitTestVisible\":")
            .Append(handle.IsHitTestVisible ? "true" : "false")
            .Append(",\"isEnabled\":");
        if (handle is Control control)
            json.Append(control.IsEnabled ? "true" : "false");
        else
            json.Append("null");
        json.Append(",\"useLayoutRounding\":")
            .Append(handle.UseLayoutRounding ? "true" : "false")
            .Append(",\"actualWidthDip\":");
        TouchContactJson.AppendNumber(json, handle.ActualWidth);
        json.Append(",\"actualHeightDip\":");
        TouchContactJson.AppendNumber(json, handle.ActualHeight);
        json.Append(",\"widthDip\":");
        TouchContactJson.AppendNumber(json, handle.Width);
        json.Append(",\"heightDip\":");
        TouchContactJson.AppendNumber(json, handle.Height);
        json.Append(",\"minWidthDip\":");
        TouchContactJson.AppendNumber(json, handle.MinWidth);
        json.Append(",\"minHeightDip\":");
        TouchContactJson.AppendNumber(json, handle.MinHeight);
        json.Append(",\"actualWidthPhysicalPx\":");
        TouchContactJson.AppendNumber(json, handle.ActualWidth * rasterizationScale);
        json.Append(",\"actualHeightPhysicalPx\":");
        TouchContactJson.AppendNumber(json, handle.ActualHeight * rasterizationScale);
    }

    private static void AppendHandlePlacement(
        StringBuilder json,
        FrameworkElement handle,
        MarkdownRendererControl renderer)
    {
        json.Append(",\"rendererLocalCornersDip\":[");
        GeneralTransform transform = handle.TransformToVisual(renderer);
        double width = handle.ActualWidth;
        double height = handle.ActualHeight;
        TouchContactJson.AppendPoint(json, transform.TransformPoint(new Point(0, 0)));
        json.Append(',');
        TouchContactJson.AppendPoint(json, transform.TransformPoint(new Point(width, 0)));
        json.Append(',');
        TouchContactJson.AppendPoint(json, transform.TransformPoint(new Point(width, height)));
        json.Append(',');
        TouchContactJson.AppendPoint(json, transform.TransformPoint(new Point(0, height)));
        // Window-root position and canvas placement: the renderer
        // snaps handles against the root, which UI Automation maps to screen.
        json.Append("],\"rootOriginDip\":");
        TouchContactJson.AppendPoint(json, handle.TransformToVisual(null).TransformPoint(new Point(0, 0)));
        json.Append(",\"canvasLeftDip\":");
        TouchContactJson.AppendNumber(json, Canvas.GetLeft(handle));
        json.Append(",\"canvasTopDip\":");
        TouchContactJson.AppendNumber(json, Canvas.GetTop(handle));
    }

    private static void AppendPeerBounds(StringBuilder json, FrameworkElement handle)
    {
        // Do not create a peer for diagnostics; observe only one the platform
        // has already materialized.
        AutomationPeer? peer = null;
        try
        {
            peer = FrameworkElementAutomationPeer.FromElement(handle);
            if (peer is not null)
            {
                TouchContactJson.AppendRect(json, peer.GetBoundingRectangle());
                json.Append(",\"peerBoundsStatus\":\"available\"");
            }
            else
            {
                json.Append("null,\"peerBoundsStatus\":\"notMaterialized\"");
            }
        }
        catch (Exception)
        {
            json.Append("null");
            json.Append(",\"peerBoundsStatus\":\"error\"");
        }
    }

    private static void AppendNearestCanvas(StringBuilder json, FrameworkElement handle)
    {
        if (FindCanvasAncestor(handle) is not { } canvas)
        {
            json.Append("null");
            return;
        }

        json.Append("{\"actualWidthDip\":");
        TouchContactJson.AppendNumber(json, canvas.ActualWidth);
        json.Append(",\"actualHeightDip\":");
        TouchContactJson.AppendNumber(json, canvas.ActualHeight);
        json.Append(",\"widthDip\":");
        TouchContactJson.AppendNumber(json, canvas.Width);
        json.Append(",\"heightDip\":");
        TouchContactJson.AppendNumber(json, canvas.Height);
        json.Append(",\"minWidthDip\":");
        TouchContactJson.AppendNumber(json, canvas.MinWidth);
        json.Append(",\"minHeightDip\":");
        TouchContactJson.AppendNumber(json, canvas.MinHeight);
        json.Append(",\"leftDip\":");
        TouchContactJson.AppendNumber(json, Canvas.GetLeft(handle));
        json.Append(",\"topDip\":");
        TouchContactJson.AppendNumber(json, Canvas.GetTop(handle));
        json.Append(",\"useLayoutRounding\":")
            .Append(canvas.UseLayoutRounding ? "true" : "false")
            .Append(",\"clipRectDip\":");
        if (canvas.Clip is { } clip)
            TouchContactJson.AppendRect(json, clip.Rect);
        else
            json.Append("null");
        json.Append('}');
    }

    private static Canvas? FindCanvasAncestor(DependencyObject element)
    {
        DependencyObject? node = VisualTreeHelper.GetParent(element);
        while (node is not null)
        {
            if (node is Canvas canvas)
                return canvas;
            node = VisualTreeHelper.GetParent(node);
        }

        return null;
    }
}

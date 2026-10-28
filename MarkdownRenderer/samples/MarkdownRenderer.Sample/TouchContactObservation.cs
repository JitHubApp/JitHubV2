using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using MarkdownRenderer.Controls;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace MarkdownRenderer.Sample;

// The observer attaches to both public viewport implementations through their shared control surface.
/// <summary>
/// Sample-only, read-only observation of touch contacts routed through the
/// active renderer. It exists so the touch UI test can distinguish a held
/// handle contact from one that was silently canceled or released.
/// </summary>
internal sealed class TouchContactObservation
{
    private const int MaximumRememberedPointers = 16;
    private const int MaximumVisualNodesToInspect = 4096;
    private const string StartHandleAutomationId = "MarkdownSelectionStartHandle";
    private const string EndHandleAutomationId = "MarkdownSelectionEndHandle";

    private readonly TextBlock _status;
    private readonly Dictionary<uint, PointerObservation> _pointers = new();
    private readonly PointerEventHandler _pressedHandler;
    private readonly PointerEventHandler _movedHandler;
    private readonly PointerEventHandler _releasedHandler;
    private readonly PointerEventHandler _canceledHandler;
    private readonly PointerEventHandler _captureLostHandler;
    private MarkdownRendererControl? _renderer;
    private bool _pointerHandlersAttached;
    private long _rendererGeneration;
    private long _eventSequence;
    private int _observationErrors;
    private int _layoutCaptureErrors;
    private long _layoutCaptureRequestSequence;
    private long _pendingLayoutCaptureRequest;
    private string _layoutCaptureJson = "{\"status\":\"notCaptured\"}";

    internal TouchContactObservation(TextBlock status)
    {
        _status = status ?? throw new ArgumentNullException(nameof(status));
        _pressedHandler = OnPointerPressed;
        _movedHandler = OnPointerMoved;
        _releasedHandler = OnPointerReleased;
        _canceledHandler = OnPointerCanceled;
        _captureLostHandler = OnPointerCaptureLost;
        PublishSnapshot();
    }

    internal void Attach(MarkdownRendererControl renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        if (ReferenceEquals(_renderer, renderer))
            return;

        Detach();
        _renderer = renderer;
        _rendererGeneration = _rendererGeneration == long.MaxValue ? 1 : _rendererGeneration + 1;
        _pointers.Clear();
        _eventSequence = 0;
        _observationErrors = 0;
        _layoutCaptureErrors = 0;
        _pendingLayoutCaptureRequest = 0;
        _layoutCaptureJson = "{\"status\":\"notCaptured\"}";
        renderer.Loaded += OnRendererLoaded;
        renderer.Unloaded += OnRendererUnloaded;
        renderer.RenderCompleted += OnRendererRenderCompleted;
        AttachPointerHandlers(renderer);
        PublishSnapshot();
    }

    internal void Detach()
    {
        if (_renderer is { } renderer)
        {
            DetachPointerHandlers(renderer);
            renderer.Loaded -= OnRendererLoaded;
            renderer.Unloaded -= OnRendererUnloaded;
            renderer.RenderCompleted -= OnRendererRenderCompleted;
            _renderer = null;
        }

        _pointers.Clear();
        _eventSequence = 0;
        _pendingLayoutCaptureRequest = 0;
        _layoutCaptureJson = "{\"status\":\"notCaptured\"}";
        PublishSnapshot();
    }

    private void OnRendererLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is MarkdownRendererControl renderer && ReferenceEquals(renderer, _renderer))
        {
            AttachPointerHandlers(renderer);
            PublishSnapshot();
        }
    }

    private void OnRendererRenderCompleted(object? sender, EventArgs args)
    {
        if (sender is MarkdownRendererControl renderer && ReferenceEquals(renderer, _renderer))
            ScheduleLayoutCapture(renderer);
    }

    private void OnRendererUnloaded(object sender, RoutedEventArgs args)
    {
        if (sender is MarkdownRendererControl renderer && ReferenceEquals(renderer, _renderer))
        {
            DetachPointerHandlers(renderer);
            _pointers.Clear();
            _eventSequence = 0;
            _pendingLayoutCaptureRequest = 0;
            _layoutCaptureJson = "{\"status\":\"unloaded\",\"rendererGeneration\":" +
                _rendererGeneration.ToString(CultureInfo.InvariantCulture) + "}";
            PublishSnapshot();
        }
    }

    private void AttachPointerHandlers(MarkdownRendererControl renderer)
    {
        if (_pointerHandlersAttached)
            return;

        renderer.AddHandler(UIElement.PointerPressedEvent, _pressedHandler, handledEventsToo: true);
        renderer.AddHandler(UIElement.PointerMovedEvent, _movedHandler, handledEventsToo: true);
        renderer.AddHandler(UIElement.PointerReleasedEvent, _releasedHandler, handledEventsToo: true);
        renderer.AddHandler(UIElement.PointerCanceledEvent, _canceledHandler, handledEventsToo: true);
        renderer.AddHandler(UIElement.PointerCaptureLostEvent, _captureLostHandler, handledEventsToo: true);
        _pointerHandlersAttached = true;
    }

    private void DetachPointerHandlers(MarkdownRendererControl renderer)
    {
        if (!_pointerHandlersAttached)
            return;

        renderer.RemoveHandler(UIElement.PointerPressedEvent, _pressedHandler);
        renderer.RemoveHandler(UIElement.PointerMovedEvent, _movedHandler);
        renderer.RemoveHandler(UIElement.PointerReleasedEvent, _releasedHandler);
        renderer.RemoveHandler(UIElement.PointerCanceledEvent, _canceledHandler);
        renderer.RemoveHandler(UIElement.PointerCaptureLostEvent, _captureLostHandler);
        _pointerHandlersAttached = false;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs args) => ObserveSafely(args, PointerEventKind.Pressed);
    private void OnPointerMoved(object sender, PointerRoutedEventArgs args) => ObserveSafely(args, PointerEventKind.Moved);
    private void OnPointerReleased(object sender, PointerRoutedEventArgs args) => ObserveSafely(args, PointerEventKind.Released);
    private void OnPointerCanceled(object sender, PointerRoutedEventArgs args) => ObserveSafely(args, PointerEventKind.Canceled);
    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs args) => ObserveSafely(args, PointerEventKind.CaptureLost);

    private void ObserveSafely(PointerRoutedEventArgs args, PointerEventKind kind)
    {
        try
        {
            if (_renderer is not { } renderer ||
                args.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Touch)
            {
                return;
            }

            uint pointerId = args.Pointer.PointerId;
            if (pointerId == 0)
                return;

            if (!TryGetOrCreatePointer(pointerId, out PointerObservation pointer))
            {
                _observationErrors = Increment(_observationErrors);
                PublishSnapshot();
                return;
            }

            // Capture routed coordinates while this pointer event is being
            // dispatched. The XamlRoot content point is stable when a viewport
            // scrolls; the renderer's UIA bounds can be clipped and therefore
            // are not a reliable screen-origin anchor. The injected API's local
            // pointer ID is not assumed to be the ID WinUI reports.
            XamlRoot xamlRoot = renderer.XamlRoot
                ?? throw new InvalidOperationException("The routed renderer has no XamlRoot.");
            UIElement rootContent = xamlRoot.Content as UIElement
                ?? throw new InvalidOperationException("The routed renderer's XamlRoot content is not a UIElement.");
            double rasterizationScale = xamlRoot.RasterizationScale;
            if (!double.IsFinite(rasterizationScale) || rasterizationScale <= 0)
                throw new InvalidOperationException("The routed renderer's XamlRoot has an invalid rasterization scale.");
            var currentPoint = args.GetCurrentPoint(renderer);
            PointerEventKind routedKind = kind;
            bool nativeCanceled = currentPoint.Properties.IsCanceled;
            // Keep both the routed event and its native cancellation flag.
            // A canceled PointerReleased is not an ordinary successful lift.
            if (nativeCanceled && kind is PointerEventKind.Pressed or PointerEventKind.Moved or PointerEventKind.Released)
            {
                kind = PointerEventKind.Canceled;
                pointer.NativeCanceledPacketCount = Increment(pointer.NativeCanceledPacketCount);
            }
            if (routedKind == PointerEventKind.Released)
                pointer.RawReleaseCount = Increment(pointer.RawReleaseCount);
            pointer.LastRoutedEvent = routedKind;
            pointer.LastNativeCanceled = nativeCanceled;
            Point currentPosition = currentPoint.Position;
            Point rootPosition = args.GetCurrentPoint(rootContent).Position;
            pointer.LastPositionXDip = currentPosition.X;
            pointer.LastPositionYDip = currentPosition.Y;
            pointer.LastRootPositionXDip = rootPosition.X;
            pointer.LastRootPositionYDip = rootPosition.Y;
            pointer.EventRasterizationScale = rasterizationScale;

            pointer.LastEventSequence = ++_eventSequence;
            pointer.LastEvent = kind;

            switch (kind)
            {
                case PointerEventKind.Pressed:
                    pointer.ContactSerial = pointer.ContactSerial == long.MaxValue ? 1 : pointer.ContactSerial + 1;
                    pointer.PressCount = Increment(pointer.PressCount);
                    pointer.InContact = true;
                    pointer.HandleAutomationId = FindSelectionHandleAutomationId(args.OriginalSource);
                    break;
                case PointerEventKind.Moved:
                    pointer.MoveCount = Increment(pointer.MoveCount);
                    if (FindSelectionHandleAutomationId(args.OriginalSource) is { } handleAutomationId)
                        pointer.HandleAutomationId = handleAutomationId;
                    pointer.InContact = currentPoint.IsInContact;
                    break;
                case PointerEventKind.Released:
                    pointer.ReleaseCount = Increment(pointer.ReleaseCount);
                    pointer.InContact = false;
                    break;
                case PointerEventKind.Canceled:
                    pointer.CancelCount = Increment(pointer.CancelCount);
                    pointer.InContact = false;
                    break;
                case PointerEventKind.CaptureLost:
                    pointer.CaptureLostCount = Increment(pointer.CaptureLostCount);
                    pointer.InContact = false;
                    break;
            }

            // Geometry is captured only around contact boundaries, never in
            // the high-frequency move path. The low-priority callback runs
            // after the renderer has had an opportunity to finish layout.
            if (kind is PointerEventKind.Pressed or PointerEventKind.Released or
                PointerEventKind.Canceled or PointerEventKind.CaptureLost)
            {
                ScheduleLayoutCapture(renderer);
            }

            PublishSnapshot();
        }
        catch (Exception)
        {
            // Publish a sticky failure count so automation cannot mistake a
            // partially updated contact for a stationary, healthy observation.
            _observationErrors = Increment(_observationErrors);
            try
            {
                PublishSnapshot();
            }
            catch (Exception)
            {
                // Diagnostics must never interrupt the routed input being observed.
            }
        }
    }

    private bool TryGetOrCreatePointer(uint pointerId, out PointerObservation pointer)
    {
        if (_pointers.TryGetValue(pointerId, out PointerObservation? existing))
        {
            pointer = existing!;
            return true;
        }

        if (_pointers.Count >= MaximumRememberedPointers)
        {
            uint oldestInactivePointerId = 0;
            long oldestSequence = long.MaxValue;
            foreach ((uint candidateId, PointerObservation candidate) in _pointers)
            {
                if (!candidate.InContact && candidate.LastEventSequence < oldestSequence)
                {
                    oldestSequence = candidate.LastEventSequence;
                    oldestInactivePointerId = candidateId;
                }
            }

            if (oldestInactivePointerId != 0)
                _pointers.Remove(oldestInactivePointerId);
            else
            {
                pointer = null!;
                return false;
            }
        }

        pointer = new PointerObservation(pointerId);
        _pointers.Add(pointerId, pointer);
        return true;
    }

    private static string? FindSelectionHandleAutomationId(object? originalSource)
    {
        if (originalSource is not DependencyObject source)
            return null;

        DependencyObject? node = source;
        while (node is not null)
        {
            string automationId = AutomationProperties.GetAutomationId(node);
            if (automationId is StartHandleAutomationId or EndHandleAutomationId)
                return automationId;
            node = VisualTreeHelper.GetParent(node);
        }

        return null;
    }

    private void ScheduleLayoutCapture(MarkdownRendererControl renderer)
    {
        if (_pendingLayoutCaptureRequest != 0)
            return;

        _layoutCaptureRequestSequence = _layoutCaptureRequestSequence == long.MaxValue
            ? 1
            : _layoutCaptureRequestSequence + 1;
        long request = _layoutCaptureRequestSequence;
        long generation = _rendererGeneration;
        _pendingLayoutCaptureRequest = request;
        _layoutCaptureJson = "{\"status\":\"pending\",\"rendererGeneration\":" +
            generation.ToString(CultureInfo.InvariantCulture) + "}";

        bool queued;
        try
        {
            queued = renderer.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                bool isCurrentRequest = _pendingLayoutCaptureRequest == request;
                if (_pendingLayoutCaptureRequest == request)
                    _pendingLayoutCaptureRequest = 0;

                if (!isCurrentRequest || generation != _rendererGeneration || !ReferenceEquals(renderer, _renderer))
                    return;

                try
                {
                    _layoutCaptureJson = CaptureLayoutSnapshotJson(renderer, generation);
                }
                catch (Exception)
                {
                    _layoutCaptureErrors = Increment(_layoutCaptureErrors);
                    _layoutCaptureJson = "{\"status\":\"error\",\"rendererGeneration\":" +
                        generation.ToString(CultureInfo.InvariantCulture) +
                        ",\"errors\":" + _layoutCaptureErrors.ToString(CultureInfo.InvariantCulture) + "}";
                }

                try
                {
                    PublishSnapshot();
                }
                catch (Exception)
                {
                    // Sample diagnostics must never affect routed input.
                    _layoutCaptureErrors = Increment(_layoutCaptureErrors);
                }
            });
        }
        catch (Exception)
        {
            queued = false;
        }

        if (!queued && _pendingLayoutCaptureRequest == request)
        {
            _pendingLayoutCaptureRequest = 0;
            _layoutCaptureErrors = Increment(_layoutCaptureErrors);
            _layoutCaptureJson = "{\"status\":\"unavailable\",\"rendererGeneration\":" +
                generation.ToString(CultureInfo.InvariantCulture) +
                ",\"errors\":" + _layoutCaptureErrors.ToString(CultureInfo.InvariantCulture) + "}";
        }
    }

    private static string CaptureLayoutSnapshotJson(MarkdownRendererControl renderer, long generation)
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
        AppendJsonNumber(json, rasterizationScale);
        json.Append(",\"rendererActualWidthDip\":");
        AppendJsonNumber(json, renderer.ActualWidth);
        json.Append(",\"rendererActualHeightDip\":");
        AppendJsonNumber(json, renderer.ActualHeight);
        json.Append(",\"snapshotSizeDip\":");
        if (renderer.CurrentSnapshot is { } snapshot)
        {
            Size size = snapshot.Size;
            json.Append("{\"width\":");
            AppendJsonNumber(json, size.Width);
            json.Append(",\"height\":");
            AppendJsonNumber(json, size.Height);
            json.Append('}');
        }
        else
        {
            json.Append("null");
        }
        json.Append(",\"ownedScrollMetrics\":");
        if (renderer.TryGetAutomationScrollMetrics(out double offset, out double viewportHeight, out double extentHeight))
        {
            json.Append("{\"offsetDip\":");
            AppendJsonNumber(json, offset);
            json.Append(",\"viewportHeightDip\":");
            AppendJsonNumber(json, viewportHeight);
            json.Append(",\"extentHeightDip\":");
            AppendJsonNumber(json, extentHeight);
            json.Append('}');
        }
        else
        {
            json.Append("null");
        }
        json.Append(",\"rendererUseLayoutRounding\":")
            .Append(renderer.UseLayoutRounding ? "true" : "false")
            .Append(",\"inspectedVisualNodes\":")
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
        json.Append("{\"automationId\":");
        AppendJsonString(json, AutomationProperties.GetAutomationId(handle));
        json.Append(",\"visibility\":");
        AppendJsonString(json, handle.Visibility.ToString());
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
        AppendJsonNumber(json, handle.ActualWidth);
        json.Append(",\"actualHeightDip\":");
        AppendJsonNumber(json, handle.ActualHeight);
        json.Append(",\"widthDip\":");
        AppendJsonNumber(json, handle.Width);
        json.Append(",\"heightDip\":");
        AppendJsonNumber(json, handle.Height);
        json.Append(",\"minWidthDip\":");
        AppendJsonNumber(json, handle.MinWidth);
        json.Append(",\"minHeightDip\":");
        AppendJsonNumber(json, handle.MinHeight);
        json.Append(",\"actualWidthPhysicalPx\":");
        AppendJsonNumber(json, handle.ActualWidth * rasterizationScale);
        json.Append(",\"actualHeightPhysicalPx\":");
        AppendJsonNumber(json, handle.ActualHeight * rasterizationScale);
        json.Append(",\"rendererLocalCornersDip\":[");

        GeneralTransform transform = handle.TransformToVisual(renderer);
        double width = handle.ActualWidth;
        double height = handle.ActualHeight;
        AppendPoint(json, transform.TransformPoint(new Point(0, 0)));
        json.Append(',');
        AppendPoint(json, transform.TransformPoint(new Point(width, 0)));
        json.Append(',');
        AppendPoint(json, transform.TransformPoint(new Point(width, height)));
        json.Append(',');
        AppendPoint(json, transform.TransformPoint(new Point(0, height)));
        // Window-root position and canvas placement: the renderer
        // snaps handles against the root, which UI Automation maps to screen.
        json.Append("],\"rootOriginDip\":");
        AppendPoint(json, handle.TransformToVisual(null).TransformPoint(new Point(0, 0)));
        json.Append(",\"canvasLeftDip\":");
        AppendJsonNumber(json, Canvas.GetLeft(handle));
        json.Append(",\"canvasTopDip\":");
        AppendJsonNumber(json, Canvas.GetTop(handle));
        json.Append(",\"peerBoundsRaw\":");

        // Do not create a peer for diagnostics; observe only one the platform
        // has already materialized.
        AutomationPeer? peer = null;
        try
        {
            peer = FrameworkElementAutomationPeer.FromElement(handle);
            if (peer is not null)
            {
                AppendJsonRect(json, peer.GetBoundingRectangle());
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

        json.Append(",\"nearestCanvas\":");
        if (FindCanvasAncestor(handle) is { } canvas)
        {
            json.Append("{\"actualWidthDip\":");
            AppendJsonNumber(json, canvas.ActualWidth);
            json.Append(",\"actualHeightDip\":");
            AppendJsonNumber(json, canvas.ActualHeight);
            json.Append(",\"widthDip\":");
            AppendJsonNumber(json, canvas.Width);
            json.Append(",\"heightDip\":");
            AppendJsonNumber(json, canvas.Height);
            json.Append(",\"minWidthDip\":");
            AppendJsonNumber(json, canvas.MinWidth);
            json.Append(",\"minHeightDip\":");
            AppendJsonNumber(json, canvas.MinHeight);
            json.Append(",\"leftDip\":");
            AppendJsonNumber(json, Canvas.GetLeft(handle));
            json.Append(",\"topDip\":");
            AppendJsonNumber(json, Canvas.GetTop(handle));
            json.Append(",\"useLayoutRounding\":")
                .Append(canvas.UseLayoutRounding ? "true" : "false")
                .Append(",\"clipRectDip\":");
            if (canvas.Clip is { } clip)
                AppendJsonRect(json, clip.Rect);
            else
                json.Append("null");
            json.Append('}');
        }
        else
        {
            json.Append("null");
        }

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

    private static void AppendPoint(StringBuilder json, Point point)
    {
        json.Append("{\"x\":");
        AppendJsonNumber(json, point.X);
        json.Append(",\"y\":");
        AppendJsonNumber(json, point.Y);
        json.Append('}');
    }

    private static void AppendJsonRect(StringBuilder json, Rect rect)
    {
        json.Append("{\"x\":");
        AppendJsonNumber(json, rect.X);
        json.Append(",\"y\":");
        AppendJsonNumber(json, rect.Y);
        json.Append(",\"width\":");
        AppendJsonNumber(json, rect.Width);
        json.Append(",\"height\":");
        AppendJsonNumber(json, rect.Height);
        json.Append('}');
    }

    private static void AppendJsonNumber(StringBuilder json, double value)
    {
        if (!double.IsFinite(value))
        {
            json.Append("null");
            return;
        }

        json.Append(value.ToString("R", CultureInfo.InvariantCulture));
    }

    private void PublishSnapshot()
    {
        var json = new StringBuilder(256);
        json.Append("{\"version\":1,\"rendererGeneration\":")
            .Append(_rendererGeneration.ToString(CultureInfo.InvariantCulture))
            .Append(",\"observationErrors\":")
            .Append(_observationErrors.ToString(CultureInfo.InvariantCulture))
            .Append(",\"observationStatus\":\"")
            .Append(_observationErrors == 0 ? "ok" : "degraded")
            .Append('"')
            .Append(",\"layoutCaptureErrors\":")
            .Append(_layoutCaptureErrors.ToString(CultureInfo.InvariantCulture))
            .Append(",\"layoutCapture\":")
            .Append(_layoutCaptureJson)
            .Append(",\"active\":[");

        bool first = true;
        foreach (PointerObservation pointer in _pointers.Values)
        {
            if (!pointer.InContact)
                continue;
            if (!first)
                json.Append(',');
            AppendPointer(json, pointer);
            first = false;
        }

        json.Append("],\"contacts\":[");
        first = true;
        foreach (PointerObservation pointer in _pointers.Values)
        {
            if (!first)
                json.Append(',');
            AppendPointer(json, pointer);
            first = false;
        }
        json.Append("]}");

        string snapshot = json.ToString();
        _status.Text = snapshot;
        AutomationProperties.SetName(_status, snapshot);
    }

    private static void AppendPointer(StringBuilder json, PointerObservation pointer)
    {
        json.Append("{\"pointerId\":")
            .Append(pointer.PointerId.ToString(CultureInfo.InvariantCulture))
            .Append(",\"contactSerial\":")
            .Append(pointer.ContactSerial.ToString(CultureInfo.InvariantCulture))
            .Append(",\"handle\":");
        if (pointer.HandleAutomationId is null)
            json.Append("null");
        else
            AppendJsonString(json, pointer.HandleAutomationId);
        json.Append(",\"pressCount\":")
            .Append(pointer.PressCount.ToString(CultureInfo.InvariantCulture))
            .Append(",\"moveCount\":")
            .Append(pointer.MoveCount.ToString(CultureInfo.InvariantCulture))
            .Append(",\"releaseCount\":")
            .Append(pointer.ReleaseCount.ToString(CultureInfo.InvariantCulture))
            .Append(",\"rawReleaseCount\":")
            .Append(pointer.RawReleaseCount.ToString(CultureInfo.InvariantCulture))
            .Append(",\"nativeCanceledPacketCount\":")
            .Append(pointer.NativeCanceledPacketCount.ToString(CultureInfo.InvariantCulture))
            .Append(",\"lastNativeCanceled\":")
            .Append(pointer.LastNativeCanceled ? "true" : "false")
            .Append(",\"cancelCount\":")
            .Append(pointer.CancelCount.ToString(CultureInfo.InvariantCulture))
            .Append(",\"captureLostCount\":")
            .Append(pointer.CaptureLostCount.ToString(CultureInfo.InvariantCulture))
            .Append(",\"inContact\":")
            .Append(pointer.InContact ? "true" : "false")
            .Append(",\"lastPositionDipX\":");
        AppendJsonNumber(json, pointer.LastPositionXDip);
        json.Append(",\"lastPositionDipY\":");
        AppendJsonNumber(json, pointer.LastPositionYDip);
        json.Append(",\"lastPositionRootDipX\":");
        AppendJsonNumber(json, pointer.LastRootPositionXDip);
        json.Append(",\"lastPositionRootDipY\":");
        AppendJsonNumber(json, pointer.LastRootPositionYDip);
        json.Append(",\"eventRasterizationScale\":");
        AppendJsonNumber(json, pointer.EventRasterizationScale);
        json.Append(",\"lastEventSequence\":")
            .Append(pointer.LastEventSequence.ToString(CultureInfo.InvariantCulture))
            .Append(",\"lastEvent\":");
        AppendJsonString(json, pointer.LastEvent.ToString());
        json.Append(",\"lastRoutedEvent\":");
        AppendJsonString(json, pointer.LastRoutedEvent.ToString());
        json.Append('}');
    }

    private static void AppendJsonString(StringBuilder json, string value)
    {
        json.Append('"');
        foreach (char character in value)
        {
            if (character is '"' or '\\')
                json.Append('\\');
            json.Append(character);
        }
        json.Append('"');
    }

    private static int Increment(int value) => value == int.MaxValue ? value : value + 1;

    private sealed class PointerObservation(uint pointerId)
    {
        internal uint PointerId { get; } = pointerId;
        internal long ContactSerial { get; set; }
        internal int PressCount { get; set; }
        internal int MoveCount { get; set; }
        internal int ReleaseCount { get; set; }
        internal int RawReleaseCount { get; set; }
        internal int NativeCanceledPacketCount { get; set; }
        internal bool LastNativeCanceled { get; set; }
        internal int CancelCount { get; set; }
        internal int CaptureLostCount { get; set; }
        internal bool InContact { get; set; }
        internal string? HandleAutomationId { get; set; }
        internal double LastPositionXDip { get; set; }
        internal double LastPositionYDip { get; set; }
        internal double LastRootPositionXDip { get; set; }
        internal double LastRootPositionYDip { get; set; }
        internal double EventRasterizationScale { get; set; }
        internal PointerEventKind LastEvent { get; set; }
        internal PointerEventKind LastRoutedEvent { get; set; }
        internal long LastEventSequence { get; set; }
    }

    private enum PointerEventKind
    {
        None,
        Pressed,
        Moved,
        Released,
        Canceled,
        CaptureLost,
    }
}

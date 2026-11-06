using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using MarkdownRenderer.Controls;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
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
    private const string NotCapturedLayoutJson = "{\"status\":\"notCaptured\"}";

    private readonly TextBlock _status;
    private readonly Dictionary<uint, TouchPointerObservation> _pointers = new();
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
    private string _layoutCaptureJson = NotCapturedLayoutJson;

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
        _layoutCaptureJson = NotCapturedLayoutJson;
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
        _layoutCaptureJson = NotCapturedLayoutJson;
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

    private void OnPointerPressed(object sender, PointerRoutedEventArgs args) => ObserveSafely(args, TouchPointerEventKind.Pressed);
    private void OnPointerMoved(object sender, PointerRoutedEventArgs args) => ObserveSafely(args, TouchPointerEventKind.Moved);
    private void OnPointerReleased(object sender, PointerRoutedEventArgs args) => ObserveSafely(args, TouchPointerEventKind.Released);
    private void OnPointerCanceled(object sender, PointerRoutedEventArgs args) => ObserveSafely(args, TouchPointerEventKind.Canceled);
    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs args) => ObserveSafely(args, TouchPointerEventKind.CaptureLost);

    private void ObserveSafely(PointerRoutedEventArgs args, TouchPointerEventKind kind)
    {
        try
        {
            Observe(args, kind);
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

    private void Observe(PointerRoutedEventArgs args, TouchPointerEventKind kind)
    {
        if (_renderer is not { } renderer ||
            args.Pointer.PointerDeviceType != PointerDeviceType.Touch)
        {
            return;
        }

        uint pointerId = args.Pointer.PointerId;
        if (pointerId == 0)
            return;

        if (!TryGetOrCreatePointer(pointerId, out TouchPointerObservation pointer))
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
        UIElement rootContent = GetRoutedRootContent(renderer, out double rasterizationScale);
        PointerPoint currentPoint = args.GetCurrentPoint(renderer);
        kind = RecordRoutedEvent(pointer, kind, currentPoint.Properties.IsCanceled);
        Point currentPosition = currentPoint.Position;
        Point rootPosition = args.GetCurrentPoint(rootContent).Position;
        pointer.LastPositionXDip = currentPosition.X;
        pointer.LastPositionYDip = currentPosition.Y;
        pointer.LastRootPositionXDip = rootPosition.X;
        pointer.LastRootPositionYDip = rootPosition.Y;
        pointer.EventRasterizationScale = rasterizationScale;

        pointer.LastEventSequence = ++_eventSequence;
        pointer.LastEvent = kind;
        ApplyContactTransition(pointer, kind, args.OriginalSource, currentPoint.IsInContact);

        // Geometry is captured only around contact boundaries, never in
        // the high-frequency move path. The low-priority callback runs
        // after the renderer has had an opportunity to finish layout.
        if (kind is TouchPointerEventKind.Pressed or TouchPointerEventKind.Released or
            TouchPointerEventKind.Canceled or TouchPointerEventKind.CaptureLost)
        {
            ScheduleLayoutCapture(renderer);
        }

        PublishSnapshot();
    }

    private static UIElement GetRoutedRootContent(MarkdownRendererControl renderer, out double rasterizationScale)
    {
        XamlRoot xamlRoot = renderer.XamlRoot
            ?? throw new InvalidOperationException("The routed renderer has no XamlRoot.");
        UIElement rootContent = xamlRoot.Content as UIElement
            ?? throw new InvalidOperationException("The routed renderer's XamlRoot content is not a UIElement.");
        rasterizationScale = xamlRoot.RasterizationScale;
        if (!double.IsFinite(rasterizationScale) || rasterizationScale <= 0)
            throw new InvalidOperationException("The routed renderer's XamlRoot has an invalid rasterization scale.");
        return rootContent;
    }

    // Keeps both the routed event and its native cancellation flag, and
    // returns the event to record: a canceled PointerReleased is not an
    // ordinary successful lift.
    private static TouchPointerEventKind RecordRoutedEvent(
        TouchPointerObservation pointer,
        TouchPointerEventKind kind,
        bool nativeCanceled)
    {
        TouchPointerEventKind routedKind = kind;
        if (nativeCanceled && kind is TouchPointerEventKind.Pressed or TouchPointerEventKind.Moved or TouchPointerEventKind.Released)
        {
            kind = TouchPointerEventKind.Canceled;
            pointer.NativeCanceledPacketCount = Increment(pointer.NativeCanceledPacketCount);
        }
        if (routedKind == TouchPointerEventKind.Released)
            pointer.RawReleaseCount = Increment(pointer.RawReleaseCount);
        pointer.LastRoutedEvent = routedKind;
        pointer.LastNativeCanceled = nativeCanceled;
        return kind;
    }

    private static void ApplyContactTransition(
        TouchPointerObservation pointer,
        TouchPointerEventKind kind,
        object? originalSource,
        bool isInContact)
    {
        switch (kind)
        {
            case TouchPointerEventKind.Pressed:
                pointer.ContactSerial = pointer.ContactSerial == long.MaxValue ? 1 : pointer.ContactSerial + 1;
                pointer.PressCount = Increment(pointer.PressCount);
                pointer.InContact = true;
                pointer.HandleAutomationId = FindSelectionHandleAutomationId(originalSource);
                break;
            case TouchPointerEventKind.Moved:
                pointer.MoveCount = Increment(pointer.MoveCount);
                if (FindSelectionHandleAutomationId(originalSource) is { } handleAutomationId)
                    pointer.HandleAutomationId = handleAutomationId;
                pointer.InContact = isInContact;
                break;
            case TouchPointerEventKind.Released:
                pointer.ReleaseCount = Increment(pointer.ReleaseCount);
                pointer.InContact = false;
                break;
            case TouchPointerEventKind.Canceled:
                pointer.CancelCount = Increment(pointer.CancelCount);
                pointer.InContact = false;
                break;
            case TouchPointerEventKind.CaptureLost:
                pointer.CaptureLostCount = Increment(pointer.CaptureLostCount);
                pointer.InContact = false;
                break;
        }
    }

    private bool TryGetOrCreatePointer(uint pointerId, out TouchPointerObservation pointer)
    {
        if (_pointers.TryGetValue(pointerId, out TouchPointerObservation? existing))
        {
            pointer = existing!;
            return true;
        }

        if (_pointers.Count >= MaximumRememberedPointers)
        {
            uint oldestInactivePointerId = 0;
            long oldestSequence = long.MaxValue;
            foreach ((uint candidateId, TouchPointerObservation candidate) in _pointers)
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

        pointer = new TouchPointerObservation(pointerId);
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
            if (automationId is TouchHandleLayoutSnapshot.StartHandleAutomationId or
                TouchHandleLayoutSnapshot.EndHandleAutomationId)
            {
                return automationId;
            }
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
            queued = renderer.DispatcherQueue.TryEnqueue(
                DispatcherQueuePriority.Low,
                () => CompleteLayoutCapture(renderer, request, generation));
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

    private void CompleteLayoutCapture(MarkdownRendererControl renderer, long request, long generation)
    {
        bool isCurrentRequest = _pendingLayoutCaptureRequest == request;
        if (_pendingLayoutCaptureRequest == request)
            _pendingLayoutCaptureRequest = 0;

        if (!isCurrentRequest || generation != _rendererGeneration || !ReferenceEquals(renderer, _renderer))
            return;

        try
        {
            _layoutCaptureJson = TouchHandleLayoutSnapshot.Capture(renderer, generation);
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
        foreach (TouchPointerObservation pointer in _pointers.Values)
        {
            if (!pointer.InContact)
                continue;
            if (!first)
                json.Append(',');
            pointer.AppendJson(json);
            first = false;
        }

        json.Append("],\"contacts\":[");
        first = true;
        foreach (TouchPointerObservation pointer in _pointers.Values)
        {
            if (!first)
                json.Append(',');
            pointer.AppendJson(json);
            first = false;
        }
        json.Append("]}");

        string snapshot = json.ToString();
        _status.Text = snapshot;
        AutomationProperties.SetName(_status, snapshot);
    }

    private static int Increment(int value) => value == int.MaxValue ? value : value + 1;
}

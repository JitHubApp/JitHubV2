using System;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Selection;
using MarkdownRenderer.Selection.Internal;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;

namespace MarkdownRenderer.Controls.Selection;

/// <summary>
/// Places the two touch selection handles at the selection's endpoints
/// during the adorner paint, and remembers where each one was placed.
/// </summary>
/// <remarks>
/// A handle whose Button holds the drag's pointer capture is left exactly
/// where it is; <see cref="ISelectionHandleDragView"/> reports which.
/// </remarks>
internal sealed class SelectionHandlePlacement
{
    private readonly ISelectionHandlesHost _host;
    private readonly FrameworkElement _renderer;
    private readonly SelectionController _selection;
    private readonly MarkdownTouchSelectionSession _touchSelection;
    private readonly SelectionHandleButtons _buttons;
    private readonly ISelectionHandleDragView _drag;

    private Point _startEndpoint;
    private Point _endEndpoint;
    private bool _startEndpointAvailable;
    private bool _endEndpointAvailable;

    /// <param name="host">The theme the handles take their size and color from.</param>
    /// <param name="renderer">The renderer element, for its flow direction and scale.</param>
    /// <param name="selection">The selection whose endpoints are shown.</param>
    /// <param name="touchSelection">Whether touch selection shows handles.</param>
    /// <param name="buttons">The two handle Buttons.</param>
    /// <param name="drag">The handle drag in progress, if any.</param>
    internal SelectionHandlePlacement(
        ISelectionHandlesHost host,
        FrameworkElement renderer,
        SelectionController selection,
        MarkdownTouchSelectionSession touchSelection,
        SelectionHandleButtons buttons,
        ISelectionHandleDragView drag)
    {
        _host = host;
        _renderer = renderer;
        _selection = selection;
        _touchSelection = touchSelection;
        _buttons = buttons;
        _drag = drag;
    }

    /// <summary>True when the start handle was placed by the last paint.</summary>
    internal bool StartEndpointAvailable => _startEndpointAvailable;

    /// <summary>True when the end handle was placed by the last paint.</summary>
    internal bool EndEndpointAvailable => _endEndpointAvailable;

    /// <summary>
    /// Marks both endpoints unplaced, so a handle press waits for the next
    /// paint to place the handles on the new layout.
    /// </summary>
    internal void MarkEndpointsUnavailable()
    {
        _startEndpointAvailable = false;
        _endEndpointAvailable = false;
    }

    /// <summary>Places the handles at the selection's endpoints while the snapshot paint lease is held.</summary>
    /// <remarks>
    /// Kept whole: each endpoint's availability, placement and visibility
    /// must be decided from the same viewport and snapshot in one pass.
    /// </remarks>
    internal void UpdateUnderPaint(LayoutSnapshot snapshot, Rect viewport)
    {
        if (snapshot.GeometryRecoveryRequired || snapshot.IsRetiringOrDisposed ||
            !_touchSelection.ShouldShowHandles || !_selection.IsActive || !_buttons.EnsureAttached())
        {
            HideUncapturedHandles();
            return;
        }

        double targetSize = Math.Clamp(
            _host.ThemeSnapshot?.MinimumInteractiveSize ?? 40,
            44,
            128);
        double dotSize = Math.Clamp(targetSize * 0.36, 12, 20);
        // The hit target keeps one physical pixel of slack over the minimum so
        // its reported size meets the minimum at any sub-pixel alignment.
        double handleExtent = SelectionController.ComputeSelectionHandleExtent(
            targetSize,
            _renderer.XamlRoot?.RasterizationScale ?? 1.0);
        Color handleColor = _host.ThemeSnapshot?.SelectionHighlightColor
            ?? Color.FromArgb(0xFF, 0x00, 0x78, 0xD4);
        bool startCaptured = _drag.IsCaptured(SelectionHandleEndpoint.Start);
        bool endCaptured = _drag.IsCaptured(SelectionHandleEndpoint.End);
        bool pointerDragActive = _drag.IsPointerDragActive;
        SelectionHandleEndpoint freeLogicalEndpoint = pointerDragActive
            ? _drag.ActiveEndpoint == SelectionHandleEndpoint.Start
                ? SelectionHandleEndpoint.End
                : SelectionHandleEndpoint.Start
            : SelectionHandleEndpoint.None;
        if (!startCaptured)
            SelectionHandleButtons.UpdateAppearance(_buttons.Start!, handleExtent, dotSize, handleColor);
        if (!endCaptured)
            SelectionHandleButtons.UpdateAppearance(_buttons.End!, handleExtent, dotSize, handleColor);

        Point startPoint = _startEndpoint;
        Point endPoint = _endEndpoint;
        bool startAvailable = startCaptured || TryGetEndpointPoint(
            snapshot,
            pointerDragActive ? freeLogicalEndpoint : SelectionHandleEndpoint.Start,
            viewport,
            targetSize / 2,
            out startPoint);
        bool endAvailable = endCaptured || TryGetEndpointPoint(
            snapshot,
            pointerDragActive ? freeLogicalEndpoint : SelectionHandleEndpoint.End,
            viewport,
            targetSize / 2,
            out endPoint);

        if (startCaptured)
        {
            // Keep the captured Button and its stored endpoint stationary until
            // release. Hiding or moving it during an edge-scroll frame can
            // synthesize PointerCaptureLost and terminate the drag.
            _startEndpointAvailable = true;
        }
        else if (startAvailable)
        {
            _startEndpoint = startPoint;
            _startEndpointAvailable = _buttons.Place(
                _buttons.Start!, startPoint, handleExtent, viewport);
        }
        else
        {
            _startEndpointAvailable = false;
            _buttons.Start!.Visibility = Visibility.Collapsed;
        }

        if (endCaptured)
        {
            _endEndpointAvailable = true;
        }
        else if (endAvailable)
        {
            _endEndpoint = endPoint;
            _endEndpointAvailable = _buttons.Place(
                _buttons.End!, endPoint, handleExtent, viewport);
        }
        else
        {
            _endEndpointAvailable = false;
            _buttons.End!.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Hides each handle that the drag does not hold captured.</summary>
    internal void HideUncapturedHandles()
    {
        if (!_drag.IsCaptured(SelectionHandleEndpoint.Start))
        {
            _startEndpointAvailable = false;
            if (_buttons.Start is not null)
                _buttons.Start.Visibility = Visibility.Collapsed;
        }

        if (!_drag.IsCaptured(SelectionHandleEndpoint.End))
        {
            _endEndpointAvailable = false;
            if (_buttons.End is not null)
                _buttons.End.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Where <paramref name="endpoint"/>'s handle was last placed, or the origin.</summary>
    internal Point GetEndpointPoint(SelectionHandleEndpoint endpoint)
        => endpoint == SelectionHandleEndpoint.Start
            ? (_startEndpointAvailable ? _startEndpoint : default)
            : (_endEndpointAvailable ? _endEndpoint : default);

    /// <summary>Hides both handles until the next paint places them again.</summary>
    internal void CollapseForPendingRefresh()
    {
        MarkEndpointsUnavailable();
        _buttons.SetVisibility(Visibility.Collapsed);
    }

    private bool TryGetEndpointPoint(
        LayoutSnapshot snapshot,
        SelectionHandleEndpoint endpoint,
        Rect viewport,
        double verticalSearchMargin,
        out Point point)
    {
        point = default;
        if (!_selection.IsActive)
            return false;

        DocumentRange normalized = _selection.Range.Normalized();
        bool rangeStart = endpoint == SelectionHandleEndpoint.Start;
        DocumentPosition position = rangeStart ? normalized.Start : normalized.End;
        if (!_selection.TryGetSelectionEndpointGeometry(
                snapshot,
                position,
                normalized,
                viewport,
                verticalSearchMargin,
                rangeStart,
                out Point caretPoint,
                out Rect rectangle,
                out bool hasCaret))
        {
            return false;
        }

        if (hasCaret)
        {
            point = SnapPoint(caretPoint);
            return double.IsFinite(point.X) && double.IsFinite(point.Y);
        }

        if (rectangle.Width <= 0 || rectangle.Height <= 0)
            return false;

        bool rightToLeft = _renderer.FlowDirection == FlowDirection.RightToLeft;
        point = rangeStart
            ? new Point(rightToLeft ? rectangle.Right : rectangle.Left, rectangle.Bottom)
            : new Point(rightToLeft ? rectangle.Left : rectangle.Right, rectangle.Bottom);
        return double.IsFinite(point.X) && double.IsFinite(point.Y);
    }

    private Point SnapPoint(Point point)
    {
        double scale = _renderer.XamlRoot?.RasterizationScale ?? 1.0;
        if (!double.IsFinite(scale) || scale <= 0)
            scale = 1.0;

        return new Point(
            Math.Round(point.X * scale) / scale,
            Math.Round(point.Y * scale) / scale);
    }
}

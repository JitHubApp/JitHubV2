using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.Storage.Streams;

namespace MarkdownRenderer.Controls;

public partial class MarkdownRendererControl
{
    // Audit rendering is deliberately bounded independently of document size.
    // The viewport target is capped at 64 MiB of pixels. Overlay readback is
    // sequential, capped at 32 MiB cumulatively, and at 16 MiB for one visual;
    // this bounds the temporary readback/CanvasBitmap copies as well.
    private const long MaximumAuditViewportPixels = 16_777_216;
    private const long MaximumAuditOverlayPixels = 8_388_608;
    private const long MaximumAuditOverlayPixelsPerElement = 4_194_304;
    private const int MaximumAuditCaptureDimension = 8192;
    private const int MaximumAuditOverlayChildren = 4096;
    private const int MaximumAuditCapturedOverlays = 256;
    private const int MaximumAuditOverlayTreeNodes = 16_384;

    private sealed class AuditOverlayCapturePlan
    {
        public required Layout.LayoutSnapshot Snapshot { get; init; }
        public required Canvas Overlay { get; init; }
        public required CanvasVirtualControl? Canvas { get; init; }
        public required FrameworkElement Root { get; init; }
        public required XamlRoot? XamlRoot { get; init; }
        public required Rect Viewport { get; init; }
        public required double ViewportTopDips { get; init; }
        public required double ViewportHeightDips { get; init; }
        public required double ViewportWidthDips { get; init; }
        public required float Dpi { get; init; }
        public required double RasterizationScale { get; init; }
        public required long PipelineGeneration { get; init; }
        public required long SnapshotGeneration { get; init; }
        public required long LayoutRevision { get; init; }
        public required List<AuditOverlayVisualStamp> DirectChildren { get; init; }
        public required List<AuditOverlayCaptureEntry> Captures { get; init; }
    }

    private sealed class AuditOverlayCaptureEntry
    {
        public required FrameworkElement Element { get; init; }
        public required Rect DocumentBounds { get; init; }
        public required int PixelWidth { get; init; }
        public required int PixelHeight { get; init; }
        public required int ZIndex { get; init; }
        public required int ChildOrdinal { get; init; }
        public required List<AuditOverlayVisualStamp> VisualTree { get; init; }
    }

    private sealed class AuditOverlayVisualStamp
    {
        public required DependencyObject Element { get; init; }
        public FrameworkElement? FrameworkElement { get; init; }
        public Rect Bounds { get; init; }
        public Point TopLeft { get; init; }
        public Point TopRight { get; init; }
        public Point BottomRight { get; init; }
        public Point BottomLeft { get; init; }
        public double ActualWidth { get; init; }
        public double ActualHeight { get; init; }
        public double Opacity { get; init; }
        public Visibility Visibility { get; init; }
        public int ZIndex { get; init; }
        public bool? IsEnabled { get; init; }
        public bool? IsChecked { get; init; }
        public object? ContentObject { get; init; }
        public string? ContentText { get; init; }
        public object? ImageSourceObject { get; init; }
        public object? ForegroundObject { get; init; }
        public object? BackgroundObject { get; init; }
    }

    internal readonly record struct AuditCapturePixelDimensions(int Width, int Height, long PixelCount)
    {
        public long ByteCount => checked(PixelCount * 4);
    }

    internal static AuditCapturePixelDimensions GetAuditCapturePixelDimensions(
        double widthDips,
        double heightDips,
        double rasterizationScale,
        long maximumPixels = MaximumAuditViewportPixels)
    {
        if (!double.IsFinite(widthDips) || widthDips <= 0 ||
            !double.IsFinite(heightDips) || heightDips <= 0 ||
            !double.IsFinite(rasterizationScale) || rasterizationScale <= 0 ||
            maximumPixels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(widthDips));
        }

        double scaledWidth = Math.Ceiling(widthDips * rasterizationScale);
        double scaledHeight = Math.Ceiling(heightDips * rasterizationScale);
        if (!double.IsFinite(scaledWidth) || !double.IsFinite(scaledHeight) ||
            scaledWidth < 1 || scaledHeight < 1 ||
            scaledWidth > MaximumAuditCaptureDimension ||
            scaledHeight > MaximumAuditCaptureDimension)
        {
            throw new InvalidOperationException("The Markdown audit pixel dimensions exceed the capture limit.");
        }

        int width = checked((int)scaledWidth);
        int height = checked((int)scaledHeight);
        long pixelCount = checked((long)width * height);
        if (pixelCount > maximumPixels)
            throw new InvalidOperationException("The Markdown audit pixel budget was exceeded.");

        return new AuditCapturePixelDimensions(width, height, pixelCount);
    }

    private AuditOverlayCapturePlan CaptureAuditOverlayPlan(
        Layout.LayoutSnapshot snapshot,
        Rect viewport,
        double viewportTopDips,
        double viewportHeightDips,
        double viewportWidthDips,
        float dpi,
        long pipelineGeneration,
        long snapshotGeneration,
        long layoutRevision)
    {
        if (DispatcherQueue?.HasThreadAccess != true)
            throw new InvalidOperationException("Audit overlay capture must run on the XAML dispatcher.");
        if (_overlay is not { } overlay || _root is not { } root)
            throw new InvalidOperationException("The Markdown audit overlay is unavailable.");

        double xamlRasterizationScale = XamlRoot?.RasterizationScale ?? 1.0;
        if (!double.IsFinite(xamlRasterizationScale) || xamlRasterizationScale <= 0 ||
            GetAuditCaptureDpi(xamlRasterizationScale) != dpi)
        {
            throw new InvalidOperationException("The Markdown audit rasterization scale changed during capture.");
        }
        double rasterizationScale = (double)dpi / 96d;

        int childCount = overlay.Children.Count;
        if ((uint)childCount > MaximumAuditOverlayChildren)
            throw new InvalidOperationException("The Markdown audit overlay child limit was exceeded.");

        var directChildren = new List<AuditOverlayVisualStamp>(childCount);
        var captures = new List<AuditOverlayCaptureEntry>();
        long totalCapturePixels = 0;
        int totalTreeNodes = 0;

        for (int ordinal = 0; ordinal < childCount; ordinal++)
        {
            UIElement child = overlay.Children[ordinal];
            if (child is not FrameworkElement element)
                throw new InvalidOperationException("The Markdown audit encountered an unsupported overlay visual.");

            AuditOverlayVisualStamp directStamp = CreateAuditOverlayVisualStamp(
                child,
                overlay,
                isCaptureRoot: false);
            directChildren.Add(directStamp);

            // RenderTargetBitmap cannot read back the adorner's composition
            // child visual; its state is painted with the snapshot instead.
            if (ReferenceEquals(child, _selectionAdorner))
                continue;

            if (element.Visibility != Visibility.Visible || element.Opacity <= 0 ||
                element.ActualWidth <= 0 || element.ActualHeight <= 0 ||
                !Intersects(viewport, directStamp.Bounds))
            {
                continue;
            }

            if (!HasPositiveAxisAlignedTransform(directStamp))
                throw new InvalidOperationException("The Markdown audit cannot composite a rotated or mirrored overlay root.");

            if (captures.Count >= MaximumAuditCapturedOverlays)
                throw new InvalidOperationException("The Markdown audit visible-overlay limit was exceeded.");

            List<AuditOverlayVisualStamp> visualTree = CaptureAuditOverlayVisualTree(
                element, overlay, ref totalTreeNodes);
            var dimensions = GetAuditCapturePixelDimensions(
                directStamp.Bounds.Width,
                directStamp.Bounds.Height,
                rasterizationScale,
                MaximumAuditOverlayPixelsPerElement);
            totalCapturePixels = checked(totalCapturePixels + dimensions.PixelCount);
            if (totalCapturePixels > MaximumAuditOverlayPixels)
                throw new InvalidOperationException("The Markdown audit overlay pixel budget was exceeded.");

            captures.Add(new AuditOverlayCaptureEntry
            {
                Element = element,
                DocumentBounds = directStamp.Bounds,
                PixelWidth = dimensions.Width,
                PixelHeight = dimensions.Height,
                ZIndex = Canvas.GetZIndex(child),
                ChildOrdinal = ordinal,
                VisualTree = visualTree,
            });
        }

        captures.Sort(static (left, right) =>
        {
            int zOrder = left.ZIndex.CompareTo(right.ZIndex);
            return zOrder != 0 ? zOrder : left.ChildOrdinal.CompareTo(right.ChildOrdinal);
        });

        return new AuditOverlayCapturePlan
        {
            Snapshot = snapshot,
            Overlay = overlay,
            Canvas = _canvas,
            Root = root,
            XamlRoot = XamlRoot,
            Viewport = viewport,
            ViewportTopDips = viewportTopDips,
            ViewportHeightDips = viewportHeightDips,
            ViewportWidthDips = viewportWidthDips,
            Dpi = dpi,
            RasterizationScale = rasterizationScale,
            PipelineGeneration = pipelineGeneration,
            SnapshotGeneration = snapshotGeneration,
            LayoutRevision = layoutRevision,
            DirectChildren = directChildren,
            Captures = captures,
        };
    }

    /// <summary>
    /// Paints the visible selection-adorner state (selection, hovered and
    /// focused link) from its committed geometry. RenderTargetBitmap cannot
    /// capture the adorner's composition child visual. Must run under the
    /// audit's snapshot paint lease.
    /// </summary>
    private void PaintAuditSelectionAdornerState(
        Layout.LayoutSnapshot snapshot,
        CanvasDrawingSession drawingSession,
        Rect viewport)
    {
        if (_selectionAdorner is not { Visibility: Visibility.Visible })
            return;

        PaintInteractiveDocumentState(snapshot, drawingSession, viewport, _selectionAdornerBackgrounds);
    }

    private async Task CaptureAuditOverlayPixelsAsync(
        CanvasRenderTarget target,
        AuditOverlayCapturePlan plan)
    {
        ValidateAuditOverlayCapturePlan(plan);
        ValidateAuditOverlayCapturePlan(plan, validateAllVisualState: true);
        if (plan.Captures.Count == 0)
            return;

        foreach (AuditOverlayCaptureEntry entry in plan.Captures)
        {
            ValidateAuditOverlayCapturePlan(plan);
            ValidateAuditOverlayVisualTree(entry, plan.Overlay);

            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(entry.Element, entry.PixelWidth, entry.PixelHeight);

            // RenderTargetBitmap has no deterministic Close/Dispose contract;
            // its output is read once and the local reference is dropped at the
            // end of this iteration. The Win2D copy and drawing session are
            // explicitly disposed below before another capture is admitted.
            ValidateAuditOverlayCapturePlan(plan);
            ValidateAuditOverlayVisualTree(entry, plan.Overlay);
            if (bitmap.PixelWidth != entry.PixelWidth || bitmap.PixelHeight != entry.PixelHeight)
                throw new InvalidOperationException("The Markdown audit overlay returned unexpected pixel dimensions.");

            IBuffer pixels = await bitmap.GetPixelsAsync();
            ValidateAuditOverlayCapturePlan(plan);
            ValidateAuditOverlayVisualTree(entry, plan.Overlay);

            long expectedBytes = checked((long)entry.PixelWidth * entry.PixelHeight * 4);
            if (pixels.Length != expectedBytes || pixels.Capacity < pixels.Length)
                throw new InvalidOperationException("The Markdown audit overlay returned an invalid pixel buffer.");

            byte[] pixelBytes = new byte[checked((int)pixels.Length)];
            using (DataReader reader = DataReader.FromBuffer(pixels))
                reader.ReadBytes(pixelBytes);

            using CanvasBitmap captured = CanvasBitmap.CreateFromBytes(
                target.Device,
                pixelBytes,
                entry.PixelWidth,
                entry.PixelHeight,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                plan.Dpi,
                CanvasAlphaMode.Premultiplied);

            using CanvasDrawingSession drawingSession = target.CreateDrawingSession();
            drawingSession.Transform = Matrix3x2.CreateTranslation(0, (float)-plan.Viewport.Top);
            drawingSession.DrawImage(captured, entry.DocumentBounds);
        }

        ValidateAuditOverlayCapturePlan(plan, validateAllVisualState: true);
    }

    private void ValidateAuditOverlayCapturePlan(
        AuditOverlayCapturePlan plan,
        bool validateAllVisualState = false)
    {
        if (DispatcherQueue?.HasThreadAccess != true ||
            _isDisposed || _isUnloaded ||
            !ReferenceEquals(_snapshot, plan.Snapshot) ||
            !ReferenceEquals(_overlay, plan.Overlay) ||
            !ReferenceEquals(_canvas, plan.Canvas) ||
            !ReferenceEquals(_root, plan.Root) ||
            !ReferenceEquals(XamlRoot, plan.XamlRoot) ||
            Volatile.Read(ref _pipelineGeneration) != plan.PipelineGeneration ||
            Volatile.Read(ref _snapshotGeneration) != plan.SnapshotGeneration ||
            plan.Snapshot.LayoutRevision != plan.LayoutRevision)
        {
            throw new InvalidOperationException("The Markdown audit visual changed during overlay capture.");
        }

        if (!TryGetViewport(out double top, out double height, out double width) ||
            !AuditViewportStateMatches(
                plan.ViewportTopDips,
                plan.ViewportHeightDips,
                plan.ViewportWidthDips,
                top,
                height,
                width))
        {
            throw new InvalidOperationException("The Markdown audit viewport changed during overlay capture.");
        }

        double xamlRasterizationScale = XamlRoot?.RasterizationScale ?? 1.0;
        if (GetAuditCaptureDpi(xamlRasterizationScale) != plan.Dpi ||
            plan.RasterizationScale != (double)plan.Dpi / 96d)
        {
            throw new InvalidOperationException("The Markdown audit DPI changed during overlay capture.");
        }

        if (!validateAllVisualState)
            return;

        if (plan.Overlay.Children.Count != plan.DirectChildren.Count)
            throw new InvalidOperationException("The Markdown audit overlay placements changed during capture.");

        for (int i = 0; i < plan.DirectChildren.Count; i++)
        {
            UIElement current = plan.Overlay.Children[i];
            AuditOverlayVisualStamp previous = plan.DirectChildren[i];
            if (!ReferenceEquals(previous.Element, current) ||
                !AuditOverlayVisualStampMatches(
                    previous,
                    CreateAuditOverlayVisualStamp(current, plan.Overlay, isCaptureRoot: false)))
            {
                throw new InvalidOperationException("The Markdown audit overlay placements changed during capture.");
            }
        }

        ValidateAuditOverlayVisualTrees(plan);
    }

    private static void ValidateAuditOverlayVisualTree(
        AuditOverlayCaptureEntry entry,
        Canvas overlay)
    {
        var pending = new Stack<DependencyObject>();
        pending.Push(entry.Element);
        int stampIndex = 0;
        while (pending.Count > 0)
        {
            DependencyObject current = pending.Pop();
            if (++stampIndex > MaximumAuditOverlayTreeNodes ||
                stampIndex > entry.VisualTree.Count || IsUnsupportedAuditCaptureVisual(current))
            {
                throw new InvalidOperationException("The Markdown audit overlay content changed during capture.");
            }

            if (!AuditOverlayVisualStampMatches(
                entry.VisualTree[stampIndex - 1],
                CreateAuditOverlayVisualStamp(current, overlay, isCaptureRoot: ReferenceEquals(current, entry.Element))))
            {
                throw new InvalidOperationException("The Markdown audit overlay content changed during capture.");
            }

            int childCount = VisualTreeHelper.GetChildrenCount(current);
            if (childCount < 0 || childCount > MaximumAuditOverlayTreeNodes - stampIndex - pending.Count)
                throw new InvalidOperationException("The Markdown audit overlay visual-tree limit was exceeded.");

            for (int i = childCount - 1; i >= 0; i--)
                pending.Push(VisualTreeHelper.GetChild(current, i));
        }

        if (stampIndex != entry.VisualTree.Count)
            throw new InvalidOperationException("The Markdown audit overlay content changed during capture.");
    }

    private static void ValidateAuditOverlayVisualTrees(AuditOverlayCapturePlan plan)
    {
        var pending = new Stack<DependencyObject>();
        int totalNodes = 0;
        foreach (AuditOverlayCaptureEntry entry in plan.Captures)
        {
            pending.Clear();
            pending.Push(entry.Element);
            int stampIndex = 0;
            while (pending.Count > 0)
            {
                DependencyObject current = pending.Pop();
                totalNodes = checked(totalNodes + 1);
                if (totalNodes > MaximumAuditOverlayTreeNodes ||
                    stampIndex >= entry.VisualTree.Count)
                {
                    throw new InvalidOperationException("The Markdown audit overlay content changed during capture.");
                }

                if (IsUnsupportedAuditCaptureVisual(current))
                    throw new InvalidOperationException("The Markdown audit contains XAML content that RenderTargetBitmap cannot capture.");

                AuditOverlayVisualStamp currentStamp = CreateAuditOverlayVisualStamp(
                    current,
                    plan.Overlay,
                    isCaptureRoot: ReferenceEquals(current, entry.Element));
                if (!AuditOverlayVisualStampMatches(entry.VisualTree[stampIndex++], currentStamp))
                    throw new InvalidOperationException("The Markdown audit overlay content changed during capture.");

                int childCount = VisualTreeHelper.GetChildrenCount(current);
                if (childCount < 0 || childCount > MaximumAuditOverlayTreeNodes - totalNodes - pending.Count)
                    throw new InvalidOperationException("The Markdown audit overlay visual-tree limit was exceeded.");

                for (int i = childCount - 1; i >= 0; i--)
                    pending.Push(VisualTreeHelper.GetChild(current, i));
            }

            if (stampIndex != entry.VisualTree.Count)
                throw new InvalidOperationException("The Markdown audit overlay content changed during capture.");
        }
    }

    private static List<AuditOverlayVisualStamp> CaptureAuditOverlayVisualTree(
        FrameworkElement root,
        Canvas overlay,
        ref int totalNodes)
    {
        var result = new List<AuditOverlayVisualStamp>();
        var pending = new Stack<DependencyObject>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            DependencyObject current = pending.Pop();
            totalNodes = checked(totalNodes + 1);
            if (totalNodes > MaximumAuditOverlayTreeNodes)
                throw new InvalidOperationException("The Markdown audit overlay visual-tree limit was exceeded.");

            if (IsUnsupportedAuditCaptureVisual(current))
                throw new InvalidOperationException("The Markdown audit contains XAML content that RenderTargetBitmap cannot capture.");

            result.Add(CreateAuditOverlayVisualStamp(
                current,
                overlay,
                isCaptureRoot: ReferenceEquals(current, root)));
            int childCount = VisualTreeHelper.GetChildrenCount(current);
            if (childCount < 0 || childCount > MaximumAuditOverlayTreeNodes - totalNodes - pending.Count)
                throw new InvalidOperationException("The Markdown audit overlay visual-tree limit was exceeded.");

            for (int i = childCount - 1; i >= 0; i--)
                pending.Push(VisualTreeHelper.GetChild(current, i));
        }

        return result;
    }

    private static AuditOverlayVisualStamp CreateAuditOverlayVisualStamp(
        DependencyObject visual,
        Canvas overlay,
        bool isCaptureRoot = false)
    {
        if (visual is not FrameworkElement element)
        {
            return new AuditOverlayVisualStamp
            {
                Element = visual,
                FrameworkElement = null,
                Bounds = default,
                TopLeft = default,
                TopRight = default,
                BottomRight = default,
                BottomLeft = default,
                ActualWidth = 0,
                ActualHeight = 0,
                Opacity = 1,
                Visibility = Visibility.Visible,
                ZIndex = 0,
            };
        }

        double width = element.ActualWidth;
        double height = element.ActualHeight;
        double opacity = element.Opacity;
        if (!double.IsFinite(width) || width < 0 ||
            !double.IsFinite(height) || height < 0 ||
            !double.IsFinite(opacity))
        {
            throw new InvalidOperationException("The Markdown audit overlay has invalid visual geometry.");
        }

        Point topLeft = default;
        Point topRight = default;
        Point bottomRight = default;
        Point bottomLeft = default;
        Rect bounds = default;
        if (width > 0 && height > 0)
        {
            GeneralTransform transform;
            try
            {
                transform = element.TransformToVisual(overlay);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException("The Markdown audit overlay has no stable visual placement.", exception);
            }

            topLeft = transform.TransformPoint(new Point(0, 0));
            topRight = transform.TransformPoint(new Point(width, 0));
            bottomRight = transform.TransformPoint(new Point(width, height));
            bottomLeft = transform.TransformPoint(new Point(0, height));
            if (!IsFinite(topLeft) || !IsFinite(topRight) ||
                !IsFinite(bottomRight) || !IsFinite(bottomLeft))
            {
                throw new InvalidOperationException("The Markdown audit overlay has an unstable visual transform.");
            }

            double left = Math.Min(Math.Min(topLeft.X, topRight.X), Math.Min(bottomRight.X, bottomLeft.X));
            double top = Math.Min(Math.Min(topLeft.Y, topRight.Y), Math.Min(bottomRight.Y, bottomLeft.Y));
            double right = Math.Max(Math.Max(topLeft.X, topRight.X), Math.Max(bottomRight.X, bottomLeft.X));
            double bottom = Math.Max(Math.Max(topLeft.Y, topRight.Y), Math.Max(bottomRight.Y, bottomLeft.Y));
            bounds = new Rect(left, top, right - left, bottom - top);
            if (!double.IsFinite(bounds.Right) || !double.IsFinite(bounds.Bottom))
                throw new InvalidOperationException("The Markdown audit overlay has invalid transformed bounds.");
        }

        bool? isEnabled = element is Control control ? control.IsEnabled : null;
        bool? isChecked = element is ToggleButton toggle ? toggle.IsChecked : null;
        object? contentObject = element is ContentControl contentControl ? contentControl.Content : null;
        var stamp = new AuditOverlayVisualStamp
        {
            Element = visual,
            FrameworkElement = element,
            Bounds = bounds,
            TopLeft = topLeft,
            TopRight = topRight,
            BottomRight = bottomRight,
            BottomLeft = bottomLeft,
            ActualWidth = width,
            ActualHeight = height,
            Opacity = opacity,
            Visibility = element.Visibility,
            ZIndex = visual is UIElement uiElement ? Canvas.GetZIndex(uiElement) : 0,
            IsEnabled = isEnabled,
            IsChecked = isChecked,
            ContentObject = contentObject is string ? null : contentObject,
            ContentText = contentObject as string ?? (element as TextBlock)?.Text,
            ImageSourceObject = element is Image image ? image.Source : null,
            ForegroundObject = element switch
            {
                Control styledControl => styledControl.Foreground,
                TextBlock textBlock => textBlock.Foreground,
                _ => null,
            },
            BackgroundObject = element is Control backgroundControl ? backgroundControl.Background : null,
        };

        if (isCaptureRoot && !HasPositiveAxisAlignedTransform(stamp))
            throw new InvalidOperationException("The Markdown audit cannot composite a rotated or mirrored overlay root.");

        return stamp;
    }

    private static bool AuditOverlayVisualStampMatches(
        AuditOverlayVisualStamp left,
        AuditOverlayVisualStamp right)
        => ReferenceEquals(left.Element, right.Element) &&
           ReferenceEquals(left.FrameworkElement, right.FrameworkElement) &&
           left.Bounds.Equals(right.Bounds) &&
           left.TopLeft.Equals(right.TopLeft) &&
           left.TopRight.Equals(right.TopRight) &&
           left.BottomRight.Equals(right.BottomRight) &&
           left.BottomLeft.Equals(right.BottomLeft) &&
           left.ActualWidth.Equals(right.ActualWidth) &&
           left.ActualHeight.Equals(right.ActualHeight) &&
           left.Opacity.Equals(right.Opacity) &&
           left.Visibility == right.Visibility &&
           left.ZIndex == right.ZIndex &&
           left.IsEnabled == right.IsEnabled &&
           left.IsChecked == right.IsChecked &&
           ReferenceEquals(left.ContentObject, right.ContentObject) &&
           ReferenceEquals(left.ImageSourceObject, right.ImageSourceObject) &&
           ReferenceEquals(left.ForegroundObject, right.ForegroundObject) &&
           ReferenceEquals(left.BackgroundObject, right.BackgroundObject) &&
           string.Equals(left.ContentText, right.ContentText, StringComparison.Ordinal);

    private static bool IsUnsupportedAuditCaptureVisual(DependencyObject visual)
        => visual is SwapChainPanel or MediaPlayerElement or WebView2;

    private static bool HasPositiveAxisAlignedTransform(AuditOverlayVisualStamp stamp)
        => stamp.TopLeft.Y == stamp.TopRight.Y &&
           stamp.TopRight.X == stamp.BottomRight.X &&
           stamp.BottomRight.Y == stamp.BottomLeft.Y &&
           stamp.BottomLeft.X == stamp.TopLeft.X &&
           stamp.TopRight.X > stamp.TopLeft.X &&
           stamp.BottomLeft.Y > stamp.TopLeft.Y;

    private static bool Intersects(Rect viewport, Rect bounds)
        => bounds.Width > 0 && bounds.Height > 0 &&
           bounds.Right > viewport.Left && bounds.Left < viewport.Right &&
           bounds.Bottom > viewport.Top && bounds.Top < viewport.Bottom;

    internal static bool AuditViewportStateMatches(
        double expectedTopDips,
        double expectedHeightDips,
        double expectedWidthDips,
        double actualTopDips,
        double actualHeightDips,
        double actualWidthDips)
        => double.IsFinite(actualTopDips) &&
           double.IsFinite(actualHeightDips) &&
           double.IsFinite(actualWidthDips) &&
           expectedTopDips.Equals(Math.Max(0, actualTopDips)) &&
           expectedHeightDips.Equals(actualHeightDips) &&
           expectedWidthDips.Equals(actualWidthDips);

    private static bool IsFinite(Point point)
        => double.IsFinite(point.X) && double.IsFinite(point.Y);
}

using MarkdownRenderer.Controls;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class SelectionAdornerScrollRefreshTests
{
    [Fact]
    public void IdleScrollSkipsButEveryAdornmentStateKeepsTheRefresh()
    {
        Assert.False(ShouldRefresh());

        // Active selection is intentionally sufficient even before any
        // selection rectangles are visible; scrolling can bring an offscreen
        // range back into the viewport.
        Assert.True(ShouldRefresh(hasActiveSelection: true));
        Assert.True(ShouldRefresh(hasHoveredLink: true));
        Assert.True(ShouldRefresh(hasFocusedLink: true));

        // A visible image or retained old rectangles must get one refresh so
        // stale pixels can be cleared after the underlying state disappears.
        Assert.True(ShouldRefresh(hasVisibleAdorner: true));
        Assert.True(ShouldRefresh(hasRetainedRectangles: true));
        Assert.True(ShouldRefresh(hasTouchHandles: true));
        Assert.True(ShouldRefresh(hasPendingRefresh: true));
    }

    [Fact]
    public void ScrollWiringSkipsIdleAdornerButKeepsLazyAndInteractiveRefreshes()
    {
        string source = File.ReadAllText(FindControlSource("MarkdownRendererControl.cs"));
        string onScroll = MethodBody(source, "private void OnScrollViewChanged(");
        int lazyLayout = onScroll.IndexOf("QueueLazyLayoutForViewport();", StringComparison.Ordinal);
        int conditionalRefresh = onScroll.IndexOf(
            "if (ShouldRefreshSelectionAdornerForScroll())",
            StringComparison.Ordinal);
        int viewportRefresh = onScroll.IndexOf("UpdateSelectionAdornerViewport();", conditionalRefresh, StringComparison.Ordinal);
        int realization = onScroll.IndexOf("RealizeVisibleEmbeds();", viewportRefresh, StringComparison.Ordinal);

        Assert.True(lazyLayout >= 0 && conditionalRefresh > lazyLayout);
        Assert.True(viewportRefresh > conditionalRefresh && realization > viewportRefresh);
        Assert.DoesNotContain("InvalidateSelectionAdorner();", onScroll, StringComparison.Ordinal);

        string shouldRefresh = MethodBody(source, "private bool ShouldRefreshSelectionAdornerForScroll()");
        Assert.Contains("_selection.IsActive", shouldRefresh, StringComparison.Ordinal);
        Assert.Contains("_lastHoveredRun is LinkRun && _lastHoveredBox is not null", shouldRefresh, StringComparison.Ordinal);
        Assert.Contains("IReadOnlyList<Layout.FocusableItem>? focusableItems = _focusableItems;", shouldRefresh, StringComparison.Ordinal);
        Assert.Contains("(uint)_focusedItemIndex < (uint)focusableItems.Count", shouldRefresh, StringComparison.Ordinal);
        Assert.Contains("focusableItems[_focusedItemIndex].Kind == Layout.FocusableItemKind.Link", shouldRefresh, StringComparison.Ordinal);
        Assert.DoesNotContain("TryGetFocusedLink(", shouldRefresh, StringComparison.Ordinal);
        Assert.Contains("_selectionAdorner?.Visibility == Visibility.Visible", shouldRefresh, StringComparison.Ordinal);
        Assert.Contains("_selectionAdornerRects.Count > 0", shouldRefresh, StringComparison.Ordinal);
        Assert.Contains("_touchSelection.ShouldShowHandles", shouldRefresh, StringComparison.Ordinal);
        Assert.Contains("_selectionAdornerRetryState.IsPending ||", shouldRefresh, StringComparison.Ordinal);
        Assert.Contains("_selectionAdornerRefreshInProgress", shouldRefresh, StringComparison.Ordinal);
        Assert.Contains("SelectionAdornerScrollRefreshPolicy.ShouldRefreshForScroll(", shouldRefresh, StringComparison.Ordinal);
    }

    [Fact]
    public void NoContentRefreshClearsStaleOverlayAndStillRepositionsTouchHandles()
    {
        string source = File.ReadAllText(FindControlSource("MarkdownRendererControl.cs"));
        string refresh = MethodBody(source, "private SelectionAdornerRefreshOperationResult RefreshSelectionAdornerUnderPaintLease(");
        int hasContent = refresh.IndexOf("bool hasContent = paintRects.Count > 0 ||", StringComparison.Ordinal);
        int noContent = refresh.IndexOf("if (!hasContent)", hasContent, StringComparison.Ordinal);
        int commitRects = refresh.IndexOf("CommitSelectionAdornerRectangles(paintRects);", noContent, StringComparison.Ordinal);
        int collapse = refresh.IndexOf("_selectionAdorner.Visibility = Visibility.Collapsed;", noContent, StringComparison.Ordinal);
        int commitViewport = refresh.IndexOf("CommitSelectionAdornerViewport(", noContent, StringComparison.Ordinal);
        int updateHandles = refresh.IndexOf("UpdateSelectionHandlesUnderPaint(snapshot, new Rect(0, top, width, height));", noContent, StringComparison.Ordinal);
        int complete = refresh.IndexOf("return SelectionAdornerRefreshOperationResult.Completed;", noContent, StringComparison.Ordinal);

        Assert.True(hasContent >= 0 && noContent > hasContent);
        Assert.True(commitRects > noContent && collapse > noContent);
        Assert.True(commitViewport > collapse && updateHandles > commitViewport && complete > updateHandles);
        Assert.Contains("if (rebuildGeometry)", refresh[..noContent], StringComparison.Ordinal);
    }

    [Fact]
    public void DeviceRecoverySuppressesFrameSubscriptionWithoutDroppingDirtyState()
    {
        string source = File.ReadAllText(FindControlSource("MarkdownRendererControl.cs"));
        string updateViewport = MethodBody(source, "private void UpdateSelectionAdornerViewport()");
        Assert.Contains("_selectionAdornerGeometryDirty = true;", updateViewport, StringComparison.Ordinal);
        Assert.Contains("_selectionAdornerVisualDirty = true;", updateViewport, StringComparison.Ordinal);
        Assert.Contains("InvalidateSelectionAdorner();", updateViewport, StringComparison.Ordinal);

        string request = MethodBody(source, "private void RequestSelectionAdornerRefresh()");
        int recoveryGuard = request.IndexOf("if (_canvasDeviceRecoveryQueued)", StringComparison.Ordinal);
        int cancel = request.IndexOf("CancelSelectionAdornerRetry();", recoveryGuard, StringComparison.Ordinal);
        int retryRequest = request.IndexOf("_selectionAdornerRetryState.Request();", recoveryGuard, StringComparison.Ordinal);
        int schedule = request.IndexOf("ScheduleSelectionAdornerRetry();", retryRequest, StringComparison.Ordinal);

        Assert.True(recoveryGuard >= 0 && cancel > recoveryGuard);
        Assert.True(retryRequest > recoveryGuard && schedule > retryRequest);
        Assert.DoesNotContain("_selectionAdornerGeometryDirty = false;", request, StringComparison.Ordinal);
        Assert.DoesNotContain("_selectionAdornerVisualDirty = false;", request, StringComparison.Ordinal);
    }

    [Fact]
    public void BusyOrReentrantRefreshRemainsEligibleForTheNextScrollFrame()
    {
        var retry = new SelectionAdornerRetryState();
        retry.Request();
        Assert.True(ShouldRefresh(hasPendingRefresh: retry.IsPending));

        Assert.True(retry.TryBeginFrame());
        retry.Retry();
        Assert.True(ShouldRefresh(hasPendingRefresh: retry.IsPending));
    }

    private static bool ShouldRefresh(
        bool hasActiveSelection = false,
        bool hasHoveredLink = false,
        bool hasFocusedLink = false,
        bool hasVisibleAdorner = false,
        bool hasRetainedRectangles = false,
        bool hasTouchHandles = false,
        bool hasPendingRefresh = false)
        => SelectionAdornerScrollRefreshPolicy.ShouldRefreshForScroll(
            hasActiveSelection,
            hasHoveredLink,
            hasFocusedLink,
            hasVisibleAdorner,
            hasRetainedRectangles,
            hasTouchHandles,
            hasPendingRefresh);

    private static string FindControlSource(string fileName)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            string direct = Path.Combine(
                directory.FullName,
                "MarkdownRenderer",
                "MarkdownRenderer",
                "Controls",
                fileName);
            if (File.Exists(direct))
                return direct;

            string nested = Path.Combine(
                directory.FullName,
                "MarkdownRenderer",
                "Controls",
                fileName);
            if (File.Exists(nested))
                return nested;
        }

        throw new FileNotFoundException($"Could not locate {fileName} from the test base directory.");
    }

    private static string MethodBody(string source, string signature)
    {
        int member = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(member >= 0, $"Could not find method '{signature}'.");
        int openingBrace = source.IndexOf('{', member + signature.Length);
        Assert.True(openingBrace >= 0, $"Could not find the body for method '{signature}'.");

        int depth = 0;
        for (int index = openingBrace; index < source.Length; index++)
        {
            if (source[index] == '{')
                depth++;
            else if (source[index] == '}' && --depth == 0)
                return source[openingBrace..(index + 1)];
        }

        throw new InvalidOperationException($"The source method body for '{signature}' is not balanced.");
    }
}

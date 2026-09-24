using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.System;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Diagnostics;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Selection;
using MarkdownRenderer.Theming;
using Markdig;

namespace MarkdownRenderer.Controls;

/// <summary>
/// The native Win2D + DirectWrite markdown renderer. Hosts a
/// <see cref="CanvasVirtualControl"/> for paint and a sibling <see cref="Canvas"/>
/// overlay for hosted WinUI embeds.
/// </summary>
[Obsolete(
    "Use MarkdownScrollView for an owned viewport or MarkdownDocumentView for an ancestor-owned viewport.",
    DiagnosticId = "MR1001",
    UrlFormat = "https://github.com/JitHubApp/JitHubV2/tree/main/docs/markdown-renderer#{0}")]
public partial class MarkdownRendererControl : UserControl, IDisposable, IMarkdownEnvironmentListener
{
    private readonly bool _ownsScrollViewport;
    private AppWindow? _windowLifecycleAppWindow;
    private WindowLifecycleSubscription? _windowLifecycleSubscription;
    private CanvasVirtualControl? _canvas;
    private Canvas? _overlay;
    private ScrollViewer? _scroll;
    private Grid? _root;
    private MarkdownEngine? _ownedEngine;
    private bool _synchronizingCodeHighlighter;
    private LegacyCodeHighlighterAdapter? _legacyCodeHighlighterAdapter;
    private ICodeHighlighter? _ownedCodeHighlighterService;
    private IDisposable? _ownedCodeHighlighter;
    private Rect _externalViewport;
    private bool _hasExternalViewport;

    private volatile LayoutSnapshot? _snapshot;
    private readonly SnapshotRetirementTracker _snapshotRetirements = new(
        static exception => MarkdownDiagnostics.WriteLine(
            $"[MarkdownRendererControl] snapshot retirement failed: {exception}"));
    private volatile CancellationTokenSource? _pipelineCts;
    private CancellationTokenSource? _imageLifetimeCts;
    private Task? _pipelineTask;
    private long _pipelineGeneration;
    private long _activePipelineGeneration;
    private long _pendingRebuildRequestTimestamp;
    private long _snapshotGeneration;
    private long _snapshotPipelineStartTimestamp;
    private long _snapshotSourceUtf16Bytes;
    private CancellationTokenSource? _lazyLayoutCts;
    private Task? _lazyLayoutTask;
    private LayoutSnapshot? _pendingLazyLayoutSnapshot;
    private LayoutSnapshot? _activeLazyLayoutSnapshot;
    private LazyLayoutBand _pendingLazyLayoutBand;
    private LazyLayoutBand _activeLazyLayoutBand;
    private double? _activeLazyLayoutAnchorOffset;
    private bool _hasPendingLazyLayoutBand;
    private bool _lazyLayoutDispatchQueued;
    private bool _pendingLazyLayoutPreserveScrollAnchor;
    private long _lazyLayoutGeneration;
    private long _appliedLazyLayoutRevision;
    private double _lastLazyViewportTop;
    private long _lastLazyViewportTimestamp;
    private double _lazyScrollVelocityPixelsPerSecond;
    private MarkdownRebuildDispatchState _rebuildDispatchState;
    private long _lastRebuildDispatchTicket;
    private bool _hasPendingRebuild;
    private bool _imageRelayoutQueued;
    private bool _imageRelayoutActive;
    private CancellationTokenSource? _imageRelayoutCts;
    private Task? _imageRelayoutTask;
    private readonly HashSet<int> _pendingImageRelayoutBlockIndices = [];
    private bool _selectionAutomationEventQueued;
    private bool _horizontalOverflowAutomationEventQueued;
    private readonly Dictionary<IHorizontalOverflowBox, PendingHorizontalOverflowAutomationChange>
        _pendingHorizontalOverflowAutomationChanges = new(ReferenceEqualityComparer.Instance);
    private RebuildReason _pendingRebuildReason = RebuildReason.Restyle;
    private float _lastWidth;
    private static readonly MarkdownTheme _defaultTheme = new();
    private static readonly MarkdownExtensionRegistry _defaultRegistry = new();
    private readonly object _parseCacheGate = new();
    private readonly Dictionary<string, bool> _disclosureStates = new(StringComparer.Ordinal);
    private readonly HashSet<HostedElementFallbackKey> _hostedElementFallbacks = new();
    private ParsedMarkdown? _parseCache;
    private string? _parseCacheSource;
    private MarkdownExtensionRegistry? _parseCacheRegistry;
    private int _parseCacheRegistryRevision = -1;
    private bool _isUpdatingDocumentProperty;
    private bool _hasExplicitDocument;
    private SizeChangedEventHandler? _sizeChangedHandler;
    private readonly SelectionController _selection = new();
    private DocumentPosition? _selectionAnchor;
    private double _lastSelectionPointerViewportY = double.NaN;

    // Selection rectangles are retained for viewport and diagnostics work. The
    // selected background and foreground are painted into one viewport-bounded
    // CanvasImageSource layered above the immutable document canvas. This keeps
    // selection drags from invalidating/repainting document tiles while avoiding
    // a second Win2D XAML control (and its independent XamlRoot lifetime).
    private readonly List<Rect> _selectionAdornerRects = new();
    private Microsoft.UI.Xaml.Controls.Image? _selectionAdorner;
    private CanvasImageSource? _selectionAdornerSource;
    private double _selectionAdornerSourceWidth;
    private double _selectionAdornerSourceHeight;
    private float _selectionAdornerSourceDpi;
    private double _selectionAdornerOffsetY;
    private Border? _selectionDragShield;
    private Button? _selectionStartHandle;
    private Button? _selectionEndHandle;
    private SelectionHandleEndpoint _activeSelectionHandle;
    private uint _selectionHandlePointerId;
    private DocumentPosition _selectionHandleFixedPosition;
    private readonly MarkdownTouchSelectionSession _touchSelection = new();
    private bool _selectionHandleFrameSubscribed;
    private long _selectionHandleLastFrameTimestamp;
    private Point _selectionHandleLatestPoint;
    private double _selectionHandleLatestViewportY;
    private Point _selectionHandleCaretOffset;
    private bool _selectionHandleMovePending;
    private long _suppressTouchTapUntilTickMs;
    private Point _suppressedTouchTapPoint;

    // Background color used to clear each canvas tile before painting. Published
    // atomically with the layout/theme snapshot so OnRegionsInvalidated (UI
    // thread, but outside the rebuild flow) never mixes presentation generations.
    // Relying on ds.Clear(Colors.Transparent) doesn't work reliably because
    // CanvasVirtualControl may not alpha-composite with the XAML compositor
    // depending on the platform's DirectX swap-chain configuration.
    private Windows.UI.Color _canvasBackground = Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);

    // Last committed theme snapshot. Used by UI operations (focus ring, etc.)
    // that need theme colors after the rebuild is complete.
    private Theming.ThemeSnapshot? _themeSnapshot;
    private MarkdownLocalizationSnapshot? _committedLocalization;
    private MarkdownEnvironmentMonitor.Subscription? _environmentSubscription;
    private MarkdownEnvironmentSnapshot _environmentSnapshot;
    private bool _canvasDeviceRecoveryQueued;
    private int _canvasDeviceRecoveryAttempt;

    // ---- Keyboard navigation ----
    // Ordered list of focusable items (LinkRuns + InlineEmbedRuns) in the current
    // snapshot.  Rebuilt after each snapshot commit.  -1 means "nothing focused".
    private System.Collections.Generic.IReadOnlyList<Layout.FocusableItem>? _focusableItems;
    private int _focusedItemIndex = -1;
    private int _focusResumeItemIndex = -1;
    private bool _taskCommandAvailabilityRefreshQueued;
    private PendingLazyFocus? _pendingLazyFocus;
    // XAML Border element used to show a focus ring around the focused item.
    // Lives on _overlay at ZIndex 1 (above selection at -1, below embeds at 0).
    private Microsoft.UI.Xaml.Controls.Border? _focusRing;
    private Windows.UI.Color _focusRingBrushColor; // cached to avoid per-keystroke SolidColorBrush allocations

    // ---- Multi-click tracking (double/triple click selection) ----
    private long _lastPressTickMs;
    private Point _lastPressPoint;
    private int _consecutiveClickCount;
    // Tracks the currently captured primary-pointer gesture. Keeping the pointer
    // id with the state makes release/cancel/capture-lost handling idempotent
    // when events arrive out of order or are routed through the drag shield.
    private PointerSession _pointerSession;
    // Set in OnUnloaded; checked in dispatcher lambdas to guard against post-unload execution.
    private bool _isUnloaded = true;
    private bool _isDisposed;
    // The provider may raise CacheInvalidated from a worker or system-event
    // callback. Keep the subscribed instance outside DependencyObject storage
    // so the callback never reads a thread-affine dependency property.
    private IMarkdownSvgRenderer? _subscribedSvgRenderer;
    private bool _canvasRenderHandlersAttached;
    private readonly PointerEventHandler _canvasPointerMovedHandler;
    private readonly PointerEventHandler _canvasPointerReleasedHandler;
    private readonly PointerEventHandler _canvasPointerCanceledHandler;
    private readonly PointerEventHandler _canvasPointerCaptureLostHandler;
    // System double-click time; read from the Win32 API at first use.
    private static readonly int _doubleClickTimeMs = GetSystemDoubleClickTimeMs();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    private static int GetSystemDoubleClickTimeMs()
    {
        try { return (int)Math.Min(GetDoubleClickTime(), (uint)int.MaxValue); } catch { return 500; }
    }

    // Click mode for the current press; governs drag-extension behaviour.
    private enum ClickMode { Single, Word, Block }
    private enum SelectionHandleEndpoint { None, Start, End }
    private ClickMode _clickMode;
    private enum RebuildReason { Full, Restyle }
    private readonly record struct PointerSession(uint PointerId, bool IsPrimary)
    {
        public bool IsActive => PointerId != 0;
    }
    private readonly record struct PendingLazyFocus(
        LayoutSnapshot Snapshot,
        Layout.FocusableItem Item,
        bool Reverse);
    private readonly record struct PendingHorizontalOverflowAutomationChange(
        LayoutSnapshot Snapshot,
        double OldPhysicalPercent,
        double NewPhysicalPercent);
    private uint _horizontalOverflowPointerId;
    private LayoutSnapshot? _horizontalOverflowSnapshot;
    private IHorizontalOverflowBox? _horizontalOverflowTarget;
    private Point _horizontalOverflowPressPoint;
    private double _horizontalOverflowInitialOffset;
    private double _horizontalOverflowThumbGrabOffset;
    private bool _horizontalOverflowCaptured;
    private bool _horizontalOverflowThumbDrag;
    // When _clickMode is Word or Block, these hold the start/end of the initially
    // selected word/block so that backward drag can correctly extend to the
    // start of the word/block under the pointer rather than always to the end.
    private DocumentPosition _dragAnchorStart;
    private DocumentPosition _dragAnchorEnd;

    // Cached cursor instances (created once, reused; disposed on Unload).
    private Microsoft.UI.Input.InputSystemCursor? _cursorHand;
    private Microsoft.UI.Input.InputSystemCursor? _cursorIBeam;

    // Cache of inline-embed rectangles (in canvas/document coordinates) plus
    // the InlineRun + owning InlineContainerBox.  Built during PlaceEmbeds
    // and consulted from pointer handlers so we can:
    //   (a) suppress our own ProtectedCursor / link-hover work over an embed
    //       so the embedded XAML element's own cursor (Hand for Button, IBeam
    //       for TextBox, …) wins;
    //   (b) avoid starting a selection on PointerPressed inside an embed so
    //       that click routes to the embed normally;
    //   (c) snap drag-through positions atomically to the run start or end,
    //       so an embed is included or excluded as a single unit.
    private readonly List<(Layout.Boxes.InlineContainerBox Box, InlineEmbedRun Run, Rect Rect)> _embedRects = new();

    // Cache of block-embed bounding rects (in document coordinates).
    // Mirrors _embedRects but for EmbedBox block elements (e.g. hosted Buttons).
    // Used by IsPointOverEmbed so that hovering a block-embed button correctly
    // suppresses link-hover and IBeam-cursor work, just like inline embeds.
    private readonly List<(BlockBox Box, Rect Rect)> _blockEmbedRects = new();

    // Code block copy buttons are overlay-hosted native controls, but they are
    // renderer chrome rather than user-authored embeds. Keep them on a separate
    // realization track so RealizedEmbedCount remains embed-only.
    private enum CodeBlockHostedElementKind
    {
        Copy,
    }

    private readonly List<(Layout.Boxes.CodeBlockBox Box, Rect Rect, CodeBlockHostedElementKind Kind)> _codeBlockActionRects = new();

    // Embed virtualisation. Plans capture each embed's position + factory
    // delegate up front; realisation happens lazily as scrolling brings them
    // into the viewport (plus an overscan band). Off-screen embeds beyond a
    // wider derealisation band are removed from the visual tree and their
    // factory's RecycleBlock hook is called. This keeps memory usage bounded
    // for very long documents with many hosted controls.
    private abstract class EmbedPlan
    {
        public Rect Rect;
        public FrameworkElement? Realized;
        public abstract object LogicalOwner { get; }
        public abstract void Realize(MarkdownRendererControl owner);
        public abstract void Derealize(MarkdownRendererControl owner);
        public abstract bool IsSameLogicalEmbed(EmbedPlan other);
        public abstract void UpdatePlacement();
    }
    private sealed class BlockEmbedPlan : EmbedPlan
    {
        public Layout.Boxes.EmbedBox Box = null!;
        public override object LogicalOwner => Box;
        public override void Realize(MarkdownRendererControl owner)
        {
            if (Realized is not null) return;
            try
            {
                var fe = Box.Factory.CreateBlock(Box.SourceBlock);
                Realized = fe;
                Box.RealizedElement = fe;
                UpdatePlacement();
                fe.KeyDown += owner.OnHostedEmbedKeyDown;
                owner._overlay!.Children.Add(fe);
                // _blockEmbedRects rebuilt in RealizeVisibleEmbeds.
            }
            catch (Exception ex)
            {
                MarkdownDiagnostics.WriteLine($"[MarkdownRendererControl] EmbedBox factory threw: {ex.Message}");
            }
        }
        public override void Derealize(MarkdownRendererControl owner)
        {
            if (Realized is null) return;
            var fe = Realized;
            fe.KeyDown -= owner.OnHostedEmbedKeyDown;
            try { owner._overlay!.Children.Remove(fe); } catch { }
            try { Box.Factory.RecycleBlock(Box.SourceBlock, fe); } catch { }
            Box.RealizedElement = null;
            Realized = null;
            // Refresh _blockEmbedRects defensively (rebuilt fully each Realize cycle from realised plans).
        }

        public override bool IsSameLogicalEmbed(EmbedPlan other)
            => other is BlockEmbedPlan block && ReferenceEquals(Box, block.Box);

        public override void UpdatePlacement()
        {
            if (Realized is null) return;
            double w = Math.Round(Box.Bounds.Width  - Box.Margin.Left - Box.Margin.Right);
            double h = Math.Round(Box.Bounds.Height - Box.Margin.Top  - Box.Margin.Bottom);
            Realized.Width = w;
            Realized.Height = h;
            Canvas.SetLeft(Realized, Math.Round(Box.Bounds.X + Box.Margin.Left));
            Canvas.SetTop(Realized, Math.Round(Box.Bounds.Y + Box.Margin.Top));
            Canvas.SetZIndex(Realized, 2);
        }
    }
    private sealed class InlineEmbedPlan : EmbedPlan
    {
        public Layout.Boxes.InlineContainerBox Icb = null!;
        public InlineEmbedRun Run = null!;
        public override object LogicalOwner => Icb;
        public override void Realize(MarkdownRendererControl owner)
        {
            if (Realized is not null) return;
            try
            {
                var fe = Run.ElementFactory();
                Realized = fe;
                Run.RealizedElement = fe;
                UpdatePlacement();
                fe.KeyDown += owner.OnHostedEmbedKeyDown;
                owner._overlay!.Children.Add(fe);
                // _embedRects rebuilt in RealizeVisibleEmbeds.
            }
            catch (Exception ex)
            {
                MarkdownDiagnostics.WriteLine($"[MarkdownRendererControl] inline embed factory threw: {ex.Message}");
            }
        }
        public override void Derealize(MarkdownRendererControl owner)
        {
            if (Realized is null) return;
            var fe = Realized;
            fe.KeyDown -= owner.OnHostedEmbedKeyDown;
            try { owner._overlay!.Children.Remove(fe); } catch { }
            try { Run.Recycle?.Invoke(fe); }
            catch (Exception ex)
            {
                MarkdownDiagnostics.WriteLine($"[MarkdownRendererControl] inline embed Recycle threw: {ex.Message}");
            }
            Run.RealizedElement = null;
            Realized = null;
        }

        public override bool IsSameLogicalEmbed(EmbedPlan other)
            => other is InlineEmbedPlan inline &&
               ReferenceEquals(Icb, inline.Icb) &&
               ReferenceEquals(Run, inline.Run);

        public override void UpdatePlacement()
        {
            if (Realized is null) return;
            double iLeft = Math.Round(Rect.X);
            double iTop  = Math.Round(Rect.Y);
            double iW    = Math.Round(Rect.X + Rect.Width)  - iLeft;
            double iH    = Math.Round(Rect.Y + Rect.Height) - iTop;
            Realized.Width = iW;
            Realized.Height = iH;
            Canvas.SetLeft(Realized, iLeft);
            Canvas.SetTop(Realized, iTop);
            Canvas.SetZIndex(Realized, 2);
        }
    }
    private readonly List<EmbedPlan> _embedPlans = new();
    private sealed class CodeBlockActionPlan
    {
        public Layout.Boxes.CodeBlockBox Box = null!;
        public Rect Rect;
        public CodeBlockHostedElementKind Kind;
        public FrameworkElement? Realized;
        private RoutedEventHandler? _clickHandler;
        private int _feedbackVersion;
        private bool _showsCopiedFeedback;

        public void Realize(MarkdownRendererControl owner)
        {
            if (Realized is not null) return;

            var (button, alreadyAttached) = owner.RentCodeBlockCopyButton();
            AutomationProperties.SetAutomationId(button, MarkdownAutomationIdentity.ForCodeCopy(Box));
            AttachHandlers(button, owner);
            button.KeyDown += owner.OnHostedEmbedKeyDown;
            Realized = button;
            Box.RealizedCopyButton = button;
            UpdatePlacement();
            if (!alreadyAttached)
                owner._overlay!.Children.Add(button);
            button.Visibility = Visibility.Visible;
            owner.RestoreFocusedCodeBlockActionIfNeeded(this);
        }

        public void Derealize(MarkdownRendererControl owner)
        {
            if (Realized is null) return;
            var fe = Realized;
            owner.PreserveLogicalFocusBeforeDerealizingCodeBlockAction(fe);
            if (fe is Button button)
            {
                if (_clickHandler is not null)
                    button.Click -= _clickHandler;
                button.KeyDown -= owner.OnHostedEmbedKeyDown;
            }

            if (_showsCopiedFeedback && fe is Button copiedButton)
            {
                owner.SetCodeBlockCopyButtonState(copiedButton, copied: false);
                _showsCopiedFeedback = false;
            }
            Box.RealizedCopyButton = null;
            Realized = null;
            _feedbackVersion++;
            if (fe is Button pooledButton)
                owner.ReturnCodeBlockCopyButton(pooledButton);
            else
            {
                try { owner._overlay!.Children.Remove(fe); } catch { }
            }
        }

        public void AdoptRealizedFrom(CodeBlockActionPlan oldPlan, MarkdownRendererControl owner)
        {
            if (oldPlan.Realized is null) return;

            var fe = oldPlan.Realized;
            if (fe is Button button)
            {
                if (oldPlan._clickHandler is not null)
                    button.Click -= oldPlan._clickHandler;
                button.KeyDown -= owner.OnHostedEmbedKeyDown;
                AttachHandlers(button, owner);
                button.KeyDown += owner.OnHostedEmbedKeyDown;
                AutomationProperties.SetAutomationId(button, MarkdownAutomationIdentity.ForCodeCopy(Box));
                owner.SetCodeBlockCopyButtonState(button, copied: false);
            }

            oldPlan._clickHandler = null;
            oldPlan._feedbackVersion++;
            oldPlan._showsCopiedFeedback = false;
            oldPlan.Box.RealizedCopyButton = null;
            oldPlan.Realized = null;

            Realized = fe;
            Box.RealizedCopyButton = fe;
            UpdatePlacement();
        }

        private void AttachHandlers(Button button, MarkdownRendererControl owner)
        {
            _clickHandler ??= (_, _) => owner.CopyCodeBlockToClipboard(this);
            button.Click += _clickHandler;
        }

        public bool IsSameLogicalAction(CodeBlockActionPlan other)
            => ReferenceEquals(Box, other.Box) && Kind == other.Kind;

        public void UpdatePlacement()
        {
            if (Realized is null) return;
            double left = Math.Round(Rect.X);
            double top = Math.Round(Rect.Y);
            double width = Math.Round(Rect.X + Rect.Width) - left;
            double height = Math.Round(Rect.Y + Rect.Height) - top;
            if (double.IsNaN(Realized.Width) || Math.Abs(Realized.Width - width) > 0.1)
                Realized.Width = width;
            if (double.IsNaN(Realized.Height) || Math.Abs(Realized.Height - height) > 0.1)
                Realized.Height = height;
            double currentLeft = Canvas.GetLeft(Realized);
            if (double.IsNaN(currentLeft) || Math.Abs(currentLeft - left) > 0.1)
                Canvas.SetLeft(Realized, left);
            double currentTop = Canvas.GetTop(Realized);
            if (double.IsNaN(currentTop) || Math.Abs(currentTop - top) > 0.1)
                Canvas.SetTop(Realized, top);
            if (Canvas.GetZIndex(Realized) != 2)
                Canvas.SetZIndex(Realized, 2);
        }

        public void ShowCopiedFeedback(MarkdownRendererControl owner)
        {
            if (Realized is not Button button)
                return;

            int version = ++_feedbackVersion;
            _showsCopiedFeedback = true;
            owner.SetCodeBlockCopyButtonState(button, copied: true);
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(1500).ConfigureAwait(false); }
                catch { return; }

                owner.DispatcherQueue.TryEnqueue(() =>
                {
                    if (owner._isUnloaded || _feedbackVersion != version)
                        return;
                    if (Realized is Button realizedButton)
                    {
                        owner.SetCodeBlockCopyButtonState(realizedButton, copied: false);
                        _showsCopiedFeedback = false;
                    }
                });
            });
        }
    }

    private readonly List<CodeBlockActionPlan> _codeBlockActionPlans = new();
    private readonly Stack<(Button Button, bool Attached)> _codeBlockCopyButtonPool = new();
    private const int CodeBlockCopyButtonPoolCapacity = 8;
    private bool _derealizingAllEmbeds;
    private readonly BoundedCodeBlockHighlightCache<CodeBlockHighlightCacheKey> _codeBlockHighlightCache = new(
        CodeBlockHighlightCacheMaxEntries,
        CodeBlockHighlightCacheBudgetBytes,
        static key => 64L + ((key.Language?.Length ?? 0) * sizeof(char)));
    private readonly HashSet<CodeBlockHighlightCacheKey> _codeBlockHighlightInFlight = new();
    private readonly SemaphoreSlim _codeBlockHighlightSemaphore = new(2, 2);
    private readonly object _codeBlockHighlightTasksGate = new();
    private readonly HashSet<Task> _codeBlockHighlightTasks = new();
    private CancellationTokenSource? _codeBlockHighlightCts;
    private Task _codeBlockHighlightRetirement = Task.CompletedTask;
    private int _codeBlockHighlightGeneration;
    private bool _promotingKeyboardFocusEntry;
    private bool _lastFocusEntryWasKeyboardTraversal;
    private bool _lastFocusEntryWasKeyboardInput;
    private bool _lastFocusEntryWasReverse;
    private bool _suppressNextFocusPromotion;
    private bool _contextMenuOpen;
    private MenuFlyout? _selectionContextMenu;
    private UIElement? _selectionDismissalRoot;
    private PointerEventHandler? _selectionDismissalPointerPressedHandler;
    private KeyEventHandler? _selectionCopyKeyDownHandler;

    // Lazy-load queue: all ImageBox instances in the current snapshot.
    // EnsureLoading() is called for each when its bounds enter the
    // viewport + LazyImageOverscanPx band.  Images already in the
    // in-memory cache start loading (no-op) immediately after build.
    private readonly List<Layout.Boxes.ImageBox> _imagePlans = new();
    private ViewportBandIndex? _imagePlanIndex;
    private readonly HashSet<Layout.Boxes.ImageBox> _subscribedImages = new();
    private string? _prefetchedMarkdownSource;
    private IMarkdownImagePrefetcher? _activeImagePrefetcher;
    private MarkdownImageResolveContext? _activeImagePrefetchContext;
    private int _prefetchedImageRegistryRevision = -1;
    private PendingImagePrefetch? _pendingImagePrefetch;
    private IMarkdownPerformanceDocumentScope? _performanceDocumentScope;
    private string? _performanceDocumentSource;
    private int _performanceRegistryRevision = -1;
    private bool _performancePrefetchStarted;

    private sealed record PendingImagePrefetch(
        Markdig.Syntax.MarkdownDocument Document,
        SafeHtmlRenderPolicy? SafeHtmlPolicy,
        IMarkdownImagePrefetcher Prefetcher,
        MarkdownImageResolveContext Context,
        CancellationToken CancellationToken);

    private const int CodeBlockHighlightCacheMaxEntries = 128;
    private const long CodeBlockHighlightCacheBudgetBytes = 8L * 1024 * 1024;
    private const double CodeBlockHighlightOverscanPx = 1600;

    /// <summary>
    /// Overscan band (pixels, each direction) within which off-screen images
    /// are preemptively loaded.  Wider than the embed virtualisation overscan
    /// so images appear before they scroll into view.
    /// </summary>
    public const double LazyImageOverscanPx = 800;

    // Stable per-LinkRun automation peer cache. UIA traversals re-request the
    // children of a block frequently; returning fresh peers each time loses
    // identity tracking (Narrator focus, "where am I"). Tied to the LinkRun
    // weakly so peers are released alongside the layout snapshot.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<LinkRun, MarkdownLinkPeer> _linkPeerCache = new();
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<InlineImageRun, MarkdownLinkedImagePeer> _linkedImagePeerCache = new();

    // Previous realised embed count — used to suppress redundant
    // EmbedsRealizationChanged fires on every scroll tick (UIA-friendly).
    private int _lastFiredRealizedCount = -1;

    /// <summary>
    /// Viewport over-scan band, in pixels, in either direction. Embeds whose
    /// bounds intersect [viewport.Top - Overscan, viewport.Bottom + Overscan]
    /// stay realised; everything else is virtualised away.
    /// </summary>
    public const double EmbedVirtualizationOverscanPx = 400;
    /// <summary>
    /// Wider band beyond which a realised embed is derealised. The gap between
    /// realisation and derealisation prevents rapid create/destroy thrash when
    /// the user scrolls along the edge of an embed.
    /// </summary>
    public const double EmbedVirtualizationDerealizeOverscanPx = 1200;

    /// <summary>
    /// Extra document pixels measured around the viewport in lazy layout mode.
    /// Wider than embed realization so text, inline image geometry, and
    /// accessibility ranges are ready before the user reaches them.
    /// </summary>
    internal const double LazyLayoutOverscanPx = 2400;

    // ---- Dependency properties ----

    /// <summary>Dependency property backing <see cref="Markdown"/>.</summary>
    public static readonly DependencyProperty MarkdownProperty =
        DependencyProperty.Register(nameof(Markdown), typeof(string), typeof(MarkdownRendererControl),
            new PropertyMetadata(string.Empty, (d, _) => ((MarkdownRendererControl)d).OnMarkdownChanged()));

    /// <summary>Gets or sets the markdown source text to render.</summary>
    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    private sealed class DeclarativeHostedElementPlan : EmbedPlan
    {
        private readonly HostedElementRealizationGate<MarkdownHostedElementRequest> _realizationGate = new();
        private MarkdownHostedElementRequest? _realizedRequest;
        private bool _fallbackRequested;

        public Layout.Boxes.DeclarativeHostedElementBox Box = null!;
        public IMarkdownHostedElementFactory Factory = null!;
        public override object LogicalOwner => Box;

        public override void Realize(MarkdownRendererControl owner)
        {
            if (Realized is not null || _fallbackRequested)
                return;

            var request = owner.CreateHostedElementRequest(Box, Rect);
            var pipelineToken = owner._pipelineCts?.Token ?? CancellationToken.None;
            long pipelineGeneration = owner._pipelineGeneration;
            if (!_realizationGate.TryBegin(request, pipelineToken, out var operation))
                return;

            ValueTask<FrameworkElement?> pending;
            try
            {
                // RealizeVisibleEmbeds is UI-thread only. Calling the public
                // factory here keeps creation of thread-affine WinUI objects on
                // the owning dispatcher while still permitting async loading.
                pending = Factory.CreateAsync(operation.Request, operation.CancellationToken);
            }
            catch (Exception ex)
            {
                CompleteFailure(owner, operation, ex, pipelineGeneration);
                return;
            }

            if (pending.IsCompletedSuccessfully)
            {
                CompleteOnUiThread(owner, operation, pending.Result, pipelineGeneration);
                return;
            }

            _ = CompleteAsync(owner, operation, pending, pipelineGeneration);
        }

        private async Task CompleteAsync(
            MarkdownRendererControl owner,
            HostedElementRealizationGate<MarkdownHostedElementRequest>.Operation operation,
            ValueTask<FrameworkElement?> pending,
            long pipelineGeneration)
        {
            FrameworkElement? element;
            try
            {
                element = await pending.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (operation.CancellationToken.IsCancellationRequested)
            {
                QueueCompletion(
                    owner,
                    operation,
                    element: null,
                    () => CompleteOnUiThread(owner, operation, null, pipelineGeneration));
                return;
            }
            catch (Exception ex)
            {
                QueueCompletion(
                    owner,
                    operation,
                    element: null,
                    () => CompleteFailure(owner, operation, ex, pipelineGeneration));
                return;
            }

            QueueCompletion(
                owner,
                operation,
                element,
                () => CompleteOnUiThread(owner, operation, element, pipelineGeneration));
        }

        private void QueueCompletion(
            MarkdownRendererControl owner,
            HostedElementRealizationGate<MarkdownHostedElementRequest>.Operation operation,
            FrameworkElement? element,
            Action completion)
        {
            var dispatcher = owner.DispatcherQueue;
            bool queued = false;
            try { queued = dispatcher is not null && dispatcher.TryEnqueue(() => completion()); }
            catch (Exception ex)
            {
                MarkdownDiagnostics.WriteLine(
                    $"[MarkdownRendererControl] hosted element completion dispatch failed: {ex.Message}");
            }
            if (queued)
                return;

            // Dispatcher shutdown means the control cannot attach the result.
            // Dispose host state as a last resort rather than leaking it.
            _realizationGate.TryClaimCompletion(operation);
            operation.Cancel();
            operation.Dispose();
            RecycleResult(operation.Request, element);
        }

        private void CompleteFailure(
            MarkdownRendererControl owner,
            HostedElementRealizationGate<MarkdownHostedElementRequest>.Operation operation,
            Exception exception,
            long pipelineGeneration)
        {
            bool wasCurrent = _realizationGate.TryClaimCompletion(operation);
            operation.Dispose();
            if (wasCurrent && owner.CanActivateHostedElementFallback(this, pipelineGeneration))
            {
                _fallbackRequested = true;
                MarkdownDiagnostics.WriteLine(
                    $"[MarkdownRendererControl] hosted element factory threw: {exception.Message}");
                owner.ActivateHostedElementFallback(this);
            }
        }

        private void CompleteOnUiThread(
            MarkdownRendererControl owner,
            HostedElementRealizationGate<MarkdownHostedElementRequest>.Operation operation,
            FrameworkElement? element,
            long pipelineGeneration)
        {
            bool isCurrent = _realizationGate.TryClaimCompletion(operation);
            bool canAttach = isCurrent &&
                !operation.CancellationToken.IsCancellationRequested &&
                owner.CanAttachHostedElement(this, pipelineGeneration);
            operation.Dispose();

            if (!canAttach)
            {
                RecycleResult(operation.Request, element);
                return;
            }

            if (element is null)
            {
                _fallbackRequested = true;
                owner.ActivateHostedElementFallback(this);
                return;
            }

            try
            {
                MarkdownRendererControl.ApplyHostedElementAutomationProperties(Box, element);
                _realizedRequest = operation.Request;
                Realized = element;
                Box.RealizedElement = element;
                UpdatePlacement();
                element.KeyDown += owner.OnHostedEmbedKeyDown;
                owner._overlay!.Children.Add(element);
            }
            catch (Exception ex)
            {
                element.KeyDown -= owner.OnHostedEmbedKeyDown;
                try { owner._overlay?.Children.Remove(element); } catch { }
                _realizedRequest = null;
                Realized = null;
                Box.RealizedElement = null;
                RecycleResult(operation.Request, element);
                MarkdownDiagnostics.WriteLine(
                    $"[MarkdownRendererControl] hosted element attachment failed: {ex.Message}");
                _fallbackRequested = true;
                owner.ActivateHostedElementFallback(this);
                return;
            }

            owner.RealizeVisibleEmbeds();
            owner.RestoreFocusedHostedElementIfNeeded(this);
        }

        public override void Derealize(MarkdownRendererControl owner)
        {
            // Clear the active slot before cancellation so a quick scroll back
            // into view can start a fresh attempt even if the old factory does
            // not observe cancellation promptly.
            _realizationGate.CancelActive();

            if (Realized is null)
                return;

            var element = Realized;
            var request = _realizedRequest;
            _realizedRequest = null;
            Realized = null;
            Box.RealizedElement = null;
            element.KeyDown -= owner.OnHostedEmbedKeyDown;
            try { owner._overlay?.Children.Remove(element); } catch { }
            if (request is not null)
                RecycleResult(request, element);
        }

        public override bool IsSameLogicalEmbed(EmbedPlan other)
            => other is DeclarativeHostedElementPlan declarative &&
               ReferenceEquals(Box, declarative.Box) &&
               ReferenceEquals(Factory, declarative.Factory);

        internal void AdoptRealizedFrom(DeclarativeHostedElementPlan oldPlan)
        {
            _realizedRequest = oldPlan._realizedRequest;
            Realized = oldPlan.Realized;
            oldPlan._realizedRequest = null;
            oldPlan.Realized = null;
            oldPlan.Box.RealizedElement = null;
            Box.RealizedElement = Realized;
        }

        private void RecycleResult(
            MarkdownHostedElementRequest request,
            FrameworkElement? element)
        {
            if (element is null)
                return;

            try { Factory.Recycle(request, element); }
            catch (Exception ex)
            {
                MarkdownDiagnostics.WriteLine(
                    $"[MarkdownRendererControl] hosted element Recycle threw: {ex.Message}");
            }
        }

        public override void UpdatePlacement()
        {
            if (Realized is null)
                return;

            double left = Math.Round(Rect.X);
            double top = Math.Round(Rect.Y);
            double width = Math.Round(Rect.Right) - left;
            double height = Math.Round(Rect.Bottom) - top;
            Realized.Width = Math.Max(0, width);
            Realized.Height = Math.Max(0, height);
            Canvas.SetLeft(Realized, left);
            Canvas.SetTop(Realized, top);
            Canvas.SetZIndex(Realized, 2);
        }
    }

    /// <summary>Dependency property backing <see cref="Document"/>.</summary>
    public static readonly DependencyProperty DocumentProperty =
        DependencyProperty.Register(
            nameof(Document),
            typeof(MarkdownRenderer.Document.MarkdownDocument),
            typeof(MarkdownRendererControl),
            new PropertyMetadata(null, (d, e) => ((MarkdownRendererControl)d).OnDocumentChanged(e)));

    /// <summary>
    /// Gets or sets an immutable, reusable parsed document. When set, the
    /// document takes precedence over <see cref="Markdown"/>. When markdown is
    /// parsed by this control, the committed snapshot is published here.
    /// </summary>
    public MarkdownRenderer.Document.MarkdownDocument? Document
    {
        get => (MarkdownRenderer.Document.MarkdownDocument?)GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    /// <summary>Dependency property backing <see cref="Engine"/>.</summary>
    public static readonly DependencyProperty EngineProperty =
        DependencyProperty.Register(
            nameof(Engine),
            typeof(MarkdownEngine),
            typeof(MarkdownRendererControl),
            new PropertyMetadata(null, (d, e) => ((MarkdownRendererControl)d).OnEngineChanged(e)));

    /// <summary>
    /// Gets or sets the immutable parser engine used for markdown replacements.
    /// A null value retains the legacy <see cref="ExtensionRegistry"/> pipeline.
    /// </summary>
    public MarkdownEngine? Engine
    {
        get => (MarkdownEngine?)GetValue(EngineProperty);
        set => SetValue(EngineProperty, value);
    }

    /// <summary>
    /// Assigns an engine created specifically for this control. Public Engine
    /// assignments are borrowed; only engines assigned through this internal
    /// ownership boundary are disposed with the control.
    /// </summary>
    internal void SetOwnedEngine(MarkdownEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        MarkdownEngine? previous = _ownedEngine;
        _ownedEngine = null;
        try
        {
            Engine = engine;
            _ownedEngine = engine;
        }
        catch
        {
            _ownedEngine = previous;
            engine.Dispose();
            throw;
        }

        if (previous is not null && !ReferenceEquals(previous, engine))
            previous.Dispose();
    }

    /// <summary>
    /// Assigns a highlighter created specifically for this control. Direct
    /// public highlighter assignments remain borrowed.
    /// </summary>
    internal void SetOwnedCodeHighlighter(ICodeHighlighter highlighter, IDisposable owner)
    {
        ArgumentNullException.ThrowIfNull(highlighter);
        ArgumentNullException.ThrowIfNull(owner);

        // Validate before changing the dependency property or ownership fields.
        // A rejected transfer leaves both the active assignment and the passed
        // owner untouched; ownership transfers only after successful assignment.
        OwnedCodeHighlighterAssignment.ValidateTransfer(
            _ownedCodeHighlighterService,
            _ownedCodeHighlighter,
            highlighter,
            owner);

        IDisposable? previousOwner = _ownedCodeHighlighter;
        ICodeHighlighter? previousService = _ownedCodeHighlighterService;
        _ownedCodeHighlighter = null;
        _ownedCodeHighlighterService = null;
        try
        {
            CodeHighlighter = highlighter;
            _ownedCodeHighlighter = owner;
            _ownedCodeHighlighterService = highlighter;
        }
        catch
        {
            _ownedCodeHighlighter = previousOwner;
            _ownedCodeHighlighterService = previousService;
            throw;
        }

        if (previousOwner is not null && !ReferenceEquals(previousOwner, owner))
            RetireOwnedCodeHighlighter(previousOwner, _codeBlockHighlightRetirement);
    }

    /// <summary>Dependency property backing <see cref="StyleSheet"/>.</summary>
    public static readonly DependencyProperty StyleSheetProperty =
        DependencyProperty.Register(
            nameof(StyleSheet),
            typeof(MarkdownStyleSheet),
            typeof(MarkdownRendererControl),
            new PropertyMetadata(null, (d, _) => ((MarkdownRendererControl)d).RequestRebuild(RebuildReason.Restyle)));

    /// <summary>Gets or sets immutable semantic style rules layered over the active theme.</summary>
    public MarkdownStyleSheet? StyleSheet
    {
        get => (MarkdownStyleSheet?)GetValue(StyleSheetProperty);
        set => SetValue(StyleSheetProperty, value);
    }

    /// <summary>Dependency property backing <see cref="StringProvider"/>.</summary>
    public static readonly DependencyProperty StringProviderProperty =
        DependencyProperty.Register(
            nameof(StringProvider),
            typeof(IMarkdownStringProvider),
            typeof(MarkdownRendererControl),
            new PropertyMetadata(null, (d, _) => ((MarkdownRendererControl)d).OnStringProviderChanged()));

    /// <summary>Gets or sets a host provider for localized visible and automation strings.</summary>
    public IMarkdownStringProvider? StringProvider
    {
        get => (IMarkdownStringProvider?)GetValue(StringProviderProperty);
        set => SetValue(StringProviderProperty, value);
    }

    /// <summary>Dependency property backing <see cref="CommandProvider"/>.</summary>
    public static readonly DependencyProperty CommandProviderProperty =
        DependencyProperty.Register(
            nameof(CommandProvider),
            typeof(IMarkdownCommandProvider),
            typeof(MarkdownRendererControl),
            new PropertyMetadata(null, (d, _) => ((MarkdownRendererControl)d).RequestRebuild()));

    /// <summary>Gets or sets a host provider for target-aware markdown commands.</summary>
    public IMarkdownCommandProvider? CommandProvider
    {
        get => (IMarkdownCommandProvider?)GetValue(CommandProviderProperty);
        set => SetValue(CommandProviderProperty, value);
    }

    /// <summary>Dependency property backing <see cref="HostedElementFactory"/>.</summary>
    public static readonly DependencyProperty HostedElementFactoryProperty =
        DependencyProperty.Register(
            nameof(HostedElementFactory),
            typeof(IMarkdownHostedElementFactory),
            typeof(MarkdownRendererControl),
            new PropertyMetadata(null, (d, _) => ((MarkdownRendererControl)d).OnHostedElementFactoryChanged()));

    /// <summary>Gets or sets the viewport-aware factory for declarative hosted content.</summary>
    public IMarkdownHostedElementFactory? HostedElementFactory
    {
        get => (IMarkdownHostedElementFactory?)GetValue(HostedElementFactoryProperty);
        set => SetValue(HostedElementFactoryProperty, value);
    }

    /// <summary>Dependency property backing <see cref="Theme"/>.</summary>
    public static readonly DependencyProperty ThemeProperty =
        DependencyProperty.Register(nameof(Theme), typeof(MarkdownTheme), typeof(MarkdownRendererControl),
            new PropertyMetadata(null, (d, e) => ((MarkdownRendererControl)d).OnThemeDpChanged(e)));

    /// <summary>Gets or sets the renderer theme.</summary>
    public MarkdownTheme? Theme
    {
        get => (MarkdownTheme?)GetValue(ThemeProperty);
        set => SetValue(ThemeProperty, value);
    }

    internal static readonly DependencyProperty ExtensionRegistryProperty =
        DependencyProperty.Register(nameof(ExtensionRegistry), typeof(MarkdownExtensionRegistry),
            typeof(MarkdownRendererControl),
            new PropertyMetadata(null, (d, _) => ((MarkdownRendererControl)d).OnParserConfigurationChanged()));

    internal MarkdownExtensionRegistry? ExtensionRegistry
    {
        get => (MarkdownExtensionRegistry?)GetValue(ExtensionRegistryProperty);
        set => SetValue(ExtensionRegistryProperty, value);
    }

    internal static readonly DependencyProperty EmbedFactoryProperty =
        DependencyProperty.Register(nameof(EmbedFactory), typeof(IMarkdownEmbedFactory),
            typeof(MarkdownRendererControl),
            new PropertyMetadata(null, (d, _) => ((MarkdownRendererControl)d).RequestRebuild()));

    internal IMarkdownEmbedFactory? EmbedFactory
    {
        get => (IMarkdownEmbedFactory?)GetValue(EmbedFactoryProperty);
        set => SetValue(EmbedFactoryProperty, value);
    }

    /// <summary>Dependency property backing <see cref="ImageResolver"/>.</summary>
    public static readonly DependencyProperty ImageResolverProperty =
        DependencyProperty.Register(nameof(ImageResolver), typeof(IMarkdownImageResolver),
            typeof(MarkdownRendererControl),
            new PropertyMetadata(null, (d, _) => ((MarkdownRendererControl)d).RequestRebuild()));

    /// <summary>Gets or sets the host-specific resolver consulted before public image loading.</summary>
    public IMarkdownImageResolver? ImageResolver
    {
        get => (IMarkdownImageResolver?)GetValue(ImageResolverProperty);
        set => SetValue(ImageResolverProperty, value);
    }

    /// <summary>Dependency property backing <see cref="SvgRenderer"/>.</summary>
    public static readonly DependencyProperty SvgRendererProperty =
        DependencyProperty.Register(nameof(SvgRenderer), typeof(IMarkdownSvgRenderer),
            typeof(MarkdownRendererControl),
            new PropertyMetadata(null, static (d, e) =>
                ((MarkdownRendererControl)d).OnSvgRendererChanged(e)));

    /// <summary>
    /// Gets or sets the shared provider used to render admitted static SVG
    /// images. The control borrows this service and never disposes it.
    /// </summary>
    public IMarkdownSvgRenderer? SvgRenderer
    {
        get => (IMarkdownSvgRenderer?)GetValue(SvgRendererProperty);
        set => SetValue(SvgRendererProperty, value);
    }

    /// <summary>Dependency property backing <see cref="PerformanceSession"/>.</summary>
    public static readonly DependencyProperty PerformanceSessionProperty =
        DependencyProperty.Register(nameof(PerformanceSession), typeof(IMarkdownPerformanceSession),
            typeof(MarkdownRendererControl),
            new PropertyMetadata(null, (d, e) =>
                ((MarkdownRendererControl)d).OnPerformanceSessionChanged(e)));

    /// <summary>
    /// Gets or sets the optional, host-owned resource preparation session. The
    /// control borrows this session and never disposes it.
    /// </summary>
    public IMarkdownPerformanceSession? PerformanceSession
    {
        get => (IMarkdownPerformanceSession?)GetValue(PerformanceSessionProperty);
        set => SetValue(PerformanceSessionProperty, value);
    }

    private void OnPerformanceSessionChanged(DependencyPropertyChangedEventArgs args)
    {
        if (args.NewValue is not null and not IMarkdownPerformanceSessionInternal)
        {
            SetValue(PerformanceSessionProperty, args.OldValue);
            throw new ArgumentException(
                "The performance session must be supplied by the optional MarkdownRenderer.Performance package.",
                nameof(PerformanceSession));
        }
        RequestRebuild();
    }

    private void OnSvgRendererChanged(DependencyPropertyChangedEventArgs args)
    {
        IMarkdownSvgRenderer? previous = Interlocked.Exchange(ref _subscribedSvgRenderer, null);
        if (previous is not null)
            previous.CacheInvalidated -= OnSvgRendererCacheInvalidated;
        if (!_isUnloaded && args.NewValue is IMarkdownSvgRenderer current)
        {
            current.CacheInvalidated -= OnSvgRendererCacheInvalidated;
            Volatile.Write(ref _subscribedSvgRenderer, current);
            current.CacheInvalidated += OnSvgRendererCacheInvalidated;
        }
        RequestRebuild();
    }

    private void OnSvgRendererCacheInvalidated(object? sender, EventArgs args)
    {
        if (!ReferenceEquals(sender, Volatile.Read(ref _subscribedSvgRenderer)) ||
            Volatile.Read(ref _isDisposed) ||
            Volatile.Read(ref _isUnloaded))
            return;

        void InvalidateProviderRasters()
        {
            if (!ReferenceEquals(sender, Volatile.Read(ref _subscribedSvgRenderer)) ||
                _isDisposed ||
                _isUnloaded)
                return;
            RequestRebuild();
            InvalidateCanvas();
        }

        if (DispatcherQueue.HasThreadAccess)
            InvalidateProviderRasters();
        else
            _ = DispatcherQueue.TryEnqueue(InvalidateProviderRasters);
    }

    /// <summary>Dependency property backing <see cref="ImageBaseUri"/>.</summary>
    public static readonly DependencyProperty ImageBaseUriProperty =
        DependencyProperty.Register(nameof(ImageBaseUri), typeof(Uri),
            typeof(MarkdownRendererControl),
            new PropertyMetadata(null, (d, _) => ((MarkdownRendererControl)d).RequestRebuild()));

    /// <summary>Gets or sets the base URI used to resolve relative image sources.</summary>
    public Uri? ImageBaseUri
    {
        get => (Uri?)GetValue(ImageBaseUriProperty);
        set => SetValue(ImageBaseUriProperty, value);
    }

    /// <summary>Dependency property backing <see cref="ImageDocumentPath"/>.</summary>
    public static readonly DependencyProperty ImageDocumentPathProperty =
        DependencyProperty.Register(nameof(ImageDocumentPath), typeof(string),
            typeof(MarkdownRendererControl),
            new PropertyMetadata(null, (d, _) => ((MarkdownRendererControl)d).RequestRebuild()));

    /// <summary>Gets or sets the source document path used by host image resolvers.</summary>
    public string? ImageDocumentPath
    {
        get => (string?)GetValue(ImageDocumentPathProperty);
        set => SetValue(ImageDocumentPathProperty, value);
    }

    /// <summary>Dependency property backing <see cref="ImageDocumentSource"/>.</summary>
    public static readonly DependencyProperty ImageDocumentSourceProperty =
        DependencyProperty.Register(nameof(ImageDocumentSource), typeof(MarkdownDocumentSource),
            typeof(MarkdownRendererControl),
            new PropertyMetadata(null, (d, _) => ((MarkdownRendererControl)d).RequestRebuild()));

    /// <summary>Gets or sets the stable identity and repository context of this document.</summary>
    public MarkdownDocumentSource? ImageDocumentSource
    {
        get => (MarkdownDocumentSource?)GetValue(ImageDocumentSourceProperty);
        set => SetValue(ImageDocumentSourceProperty, value);
    }

    /// <summary>Dependency property backing <see cref="AllowThirdPartyRemoteImages"/>.</summary>
    public static readonly DependencyProperty AllowThirdPartyRemoteImagesProperty =
        DependencyProperty.Register(
            nameof(AllowThirdPartyRemoteImages),
            typeof(bool),
            typeof(MarkdownRendererControl),
            new PropertyMetadata(false, (d, _) => ((MarkdownRendererControl)d).RequestRebuild()));

    /// <summary>
    /// Gets or sets whether the host's image policy permits third-party HTTPS images.
    /// The default is <see langword="false"/>. A host can set this once as an application
    /// policy or enable it after obtaining per-document consent. Insecure HTTP images
    /// remain blocked regardless of this value.
    /// </summary>
    public bool AllowThirdPartyRemoteImages
    {
        get => (bool)GetValue(AllowThirdPartyRemoteImagesProperty);
        set => SetValue(AllowThirdPartyRemoteImagesProperty, value);
    }

    /// <summary>Dependency property backing <see cref="IsSelectionEnabled"/>.</summary>
    public static readonly DependencyProperty IsSelectionEnabledProperty =
        DependencyProperty.Register(nameof(IsSelectionEnabled), typeof(bool),
            typeof(MarkdownRendererControl), new PropertyMetadata(true, static (d, e) =>
                ((MarkdownRendererControl)d).OnSelectionEnabledChanged((bool)e.NewValue)));

    /// <summary>
    /// Gets or sets whether read-only text selection is available to mouse,
    /// keyboard, pen, touch, and UI Automation clients.
    /// </summary>
    public bool IsSelectionEnabled
    {
        get => (bool)GetValue(IsSelectionEnabledProperty);
        set => SetValue(IsSelectionEnabledProperty, value);
    }

    /// <summary>Dependency property backing <see cref="IsCodeBlockCopyEnabled"/>.</summary>
    public static readonly DependencyProperty IsCodeBlockCopyEnabledProperty =
        DependencyProperty.Register(nameof(IsCodeBlockCopyEnabled), typeof(bool),
            typeof(MarkdownRendererControl), new PropertyMetadata(true, (d, _) => ((MarkdownRendererControl)d).RequestRebuild()));

    /// <summary>Gets or sets whether code blocks include an always-visible copy button.</summary>
    public bool IsCodeBlockCopyEnabled
    {
        get => (bool)GetValue(IsCodeBlockCopyEnabledProperty);
        set => SetValue(IsCodeBlockCopyEnabledProperty, value);
    }

    /// <summary>Dependency property backing <see cref="CodeBlockCopyButtonLabel"/>.</summary>
    public static readonly DependencyProperty CodeBlockCopyButtonLabelProperty =
        DependencyProperty.Register(nameof(CodeBlockCopyButtonLabel), typeof(string),
            typeof(MarkdownRendererControl), new PropertyMetadata(null, (d, _) => ((MarkdownRendererControl)d).UpdateCodeBlockCopyButtonLabels()));

    /// <summary>
    /// Gets or sets the accessible label and tooltip text used for code-block copy buttons.
    /// A null or whitespace value uses the renderer's localized default.
    /// </summary>
    public string? CodeBlockCopyButtonLabel
    {
        get => (string?)GetValue(CodeBlockCopyButtonLabelProperty);
        set => SetValue(CodeBlockCopyButtonLabelProperty, value);
    }

    /// <summary>Dependency property backing <see cref="CodeBlockCopiedButtonLabel"/>.</summary>
    public static readonly DependencyProperty CodeBlockCopiedButtonLabelProperty =
        DependencyProperty.Register(nameof(CodeBlockCopiedButtonLabel), typeof(string),
            typeof(MarkdownRendererControl), new PropertyMetadata(null, (d, _) => ((MarkdownRendererControl)d).UpdateCodeBlockCopyButtonLabels()));

    /// <summary>
    /// Gets or sets the accessible label and tooltip text announced briefly after a code block is copied.
    /// A null or whitespace value uses the renderer's localized default.
    /// </summary>
    public string? CodeBlockCopiedButtonLabel
    {
        get => (string?)GetValue(CodeBlockCopiedButtonLabelProperty);
        set => SetValue(CodeBlockCopiedButtonLabelProperty, value);
    }

    /// <summary>Dependency property backing <see cref="CodeBlockCopyButtonStyle"/>.</summary>
    public static readonly DependencyProperty CodeBlockCopyButtonStyleProperty =
        DependencyProperty.Register(
            nameof(CodeBlockCopyButtonStyle),
            typeof(Style),
            typeof(MarkdownRendererControl),
            new PropertyMetadata(null, (d, _) => ((MarkdownRendererControl)d).UpdateCodeBlockCopyButtonStyles()));

    /// <summary>Gets or sets the host-provided style for code-block copy buttons.</summary>
    public Style? CodeBlockCopyButtonStyle
    {
        get => (Style?)GetValue(CodeBlockCopyButtonStyleProperty);
        set => SetValue(CodeBlockCopyButtonStyleProperty, value);
    }

    /// <summary>Dependency property backing <see cref="IsCodeBlockSyntaxHighlightingEnabled"/>.</summary>
    public static readonly DependencyProperty IsCodeBlockSyntaxHighlightingEnabledProperty =
        DependencyProperty.Register(nameof(IsCodeBlockSyntaxHighlightingEnabled), typeof(bool),
            typeof(MarkdownRendererControl), new PropertyMetadata(true, (d, _) => ((MarkdownRendererControl)d).RequestRebuild()));

    /// <summary>Gets or sets whether code blocks may request syntax highlighting from a configured provider.</summary>
    public bool IsCodeBlockSyntaxHighlightingEnabled
    {
        get => (bool)GetValue(IsCodeBlockSyntaxHighlightingEnabledProperty);
        set => SetValue(IsCodeBlockSyntaxHighlightingEnabledProperty, value);
    }

    /// <summary>Dependency property backing <see cref="CodeBlockSyntaxHighlighter"/>.</summary>
    [Obsolete("Use CodeHighlighterProperty. This compatibility alias shares the same provider backing.")]
    public static readonly DependencyProperty CodeBlockSyntaxHighlighterProperty =
        DependencyProperty.Register(nameof(CodeBlockSyntaxHighlighter), typeof(ICodeBlockSyntaxHighlighter),
            typeof(MarkdownRendererControl), new PropertyMetadata(null, (d, e) => ((MarkdownRendererControl)d).OnLegacyCodeHighlighterChanged(e)));

    /// <summary>
    /// Gets or sets the preview syntax-highlighting provider. This compatibility
    /// alias is adapted to <see cref="CodeHighlighter"/> and does not create a
    /// second highlighting pipeline.
    /// </summary>
    [Obsolete("Use CodeHighlighter for explicit cancellation support.")]
    public ICodeBlockSyntaxHighlighter? CodeBlockSyntaxHighlighter
    {
        get => (ICodeBlockSyntaxHighlighter?)GetValue(CodeBlockSyntaxHighlighterProperty);
        set => SetValue(CodeBlockSyntaxHighlighterProperty, value);
    }

    /// <summary>Dependency property backing <see cref="IsTaskListEditingEnabled"/>.</summary>
    public static readonly DependencyProperty IsTaskListEditingEnabledProperty =
        DependencyProperty.Register(
            nameof(IsTaskListEditingEnabled),
            typeof(bool),
            typeof(MarkdownRendererControl),
            new PropertyMetadata(false, (d, _) => ((MarkdownRendererControl)d).RequestRebuild()));

    /// <summary>
    /// Gets or sets whether task-list markers may invoke host-provided
    /// <see cref="MarkdownCommandKind.ToggleTask"/> commands. The default is
    /// false and exposes read-only task semantics without UIA TogglePattern.
    /// </summary>
    public bool IsTaskListEditingEnabled
    {
        get => (bool)GetValue(IsTaskListEditingEnabledProperty);
        set => SetValue(IsTaskListEditingEnabledProperty, value);
    }

    /// <summary>Dependency property backing <see cref="CodeHighlighter"/>.</summary>
    public static readonly DependencyProperty CodeHighlighterProperty =
        DependencyProperty.Register(
            nameof(CodeHighlighter),
            typeof(ICodeHighlighter),
            typeof(MarkdownRendererControl),
            new PropertyMetadata(null, static (d, e) =>
                ((MarkdownRendererControl)d).OnCodeHighlighterChanged(e)));

    /// <summary>
    /// Gets or sets the stable asynchronous, cancellation-aware code highlighter.
    /// </summary>
    public ICodeHighlighter? CodeHighlighter
    {
        get => (ICodeHighlighter?)GetValue(CodeHighlighterProperty);
        set => SetValue(CodeHighlighterProperty, value);
    }

    /// <summary>Dependency property backing <see cref="CodeBlockLineNumberMode"/>.</summary>
    public static readonly DependencyProperty CodeBlockLineNumberModeProperty =
        DependencyProperty.Register(nameof(CodeBlockLineNumberMode), typeof(CodeBlockLineNumberMode),
            typeof(MarkdownRendererControl), new PropertyMetadata(CodeBlockLineNumberMode.AutoMultiline, (d, _) => ((MarkdownRendererControl)d).RequestRebuild()));

    /// <summary>Gets or sets when code blocks show line numbers.</summary>
    public CodeBlockLineNumberMode CodeBlockLineNumberMode
    {
        get => (CodeBlockLineNumberMode)GetValue(CodeBlockLineNumberModeProperty);
        set => SetValue(CodeBlockLineNumberModeProperty, value);
    }

    /// <summary>Dependency property backing <see cref="CodeBlockWrappingMode"/>.</summary>
    public static readonly DependencyProperty CodeBlockWrappingModeProperty =
        DependencyProperty.Register(
            nameof(CodeBlockWrappingMode),
            typeof(CodeBlockWrappingMode),
            typeof(MarkdownRendererControl),
            new PropertyMetadata(
                CodeBlockWrappingMode.NoWrap,
                (d, _) => ((MarkdownRendererControl)d).RequestRebuild()));

    /// <summary>
    /// Gets or sets whether long code lines remain unwrapped with a local
    /// horizontal viewport or wrap to the available width.
    /// </summary>
    public CodeBlockWrappingMode CodeBlockWrappingMode
    {
        get => (CodeBlockWrappingMode)GetValue(CodeBlockWrappingModeProperty);
        set => SetValue(CodeBlockWrappingModeProperty, value);
    }

    /// <summary>
    /// Creates a renderer configured for the core CommonMark feature set.
    /// </summary>
    /// <param name="markdown">Initial markdown source text.</param>
    /// <param name="theme">Theme to assign, or null to use the renderer default.</param>
    /// <param name="isSelectionEnabled">True to enable text selection.</param>
    /// <param name="isCodeBlockCopyEnabled">True to show copy buttons on code blocks.</param>
    /// <param name="codeBlockSyntaxHighlighter">Optional code-block syntax-highlighting provider.</param>
    /// <param name="codeBlockCopyButtonLabel">Accessible label and tooltip for code-block copy buttons, or null for the localized default.</param>
    /// <param name="codeBlockCopiedButtonLabel">Accessible label and tooltip after copy succeeds, or null for the localized default.</param>
    /// <param name="imageResolver">Optional host-specific image resolver used before public URI loading.</param>
    /// <param name="imageBaseUri">Optional base URI used to resolve relative image sources.</param>
    /// <param name="imageDocumentPath">Optional source document path used by host image resolvers.</param>
    /// <param name="codeBlockWrappingMode">Long-line wrapping policy. The default preserves lines and enables local horizontal scrolling.</param>
    /// <returns>A new configured viewport-owning renderer.</returns>
    public static MarkdownScrollView CreateDefault(
        string? markdown = null,
        MarkdownTheme? theme = null,
        bool isSelectionEnabled = true,
        bool isCodeBlockCopyEnabled = true,
        ICodeBlockSyntaxHighlighter? codeBlockSyntaxHighlighter = null,
        string? codeBlockCopyButtonLabel = null,
        string? codeBlockCopiedButtonLabel = null,
        IMarkdownImageResolver? imageResolver = null,
        Uri? imageBaseUri = null,
        string? imageDocumentPath = null,
        CodeBlockWrappingMode codeBlockWrappingMode = CodeBlockWrappingMode.NoWrap)
        => new MarkdownRendererControlBuilder()
            .WithMarkdown(markdown)
            .WithTheme(theme)
            .WithImageResolver(imageResolver)
            .WithImageBaseUri(imageBaseUri)
            .WithImageDocumentPath(imageDocumentPath)
            .WithSelectionEnabled(isSelectionEnabled)
            .WithCodeBlockCopyEnabled(isCodeBlockCopyEnabled)
            .WithCodeBlockCopyButtonLabel(codeBlockCopyButtonLabel)
            .WithCodeBlockCopiedButtonLabel(codeBlockCopiedButtonLabel)
            .WithCodeBlockSyntaxHighlighter(codeBlockSyntaxHighlighter)
            .WithCodeBlockWrappingMode(codeBlockWrappingMode)
            .BuildScrollView();

    internal MarkdownLinkPeer GetOrCreateLinkPeer(MarkdownBlockPeer parent, LinkRun run)
    {
        // GetValue is atomic for concurrent UIA callers and avoids the
        // TryGetValue+Add race. The factory captures the *current* parent
        // peer; this is acceptable because the parent peer's bounding-rect
        // computation only reads through to the live owner control + box,
        // so a "stale" parent reference still resolves to the right rect.
        return _linkPeerCache.GetValue(run, r => new MarkdownLinkPeer(this, parent, r));
    }

    internal MarkdownLinkedImagePeer GetOrCreateLinkedImagePeer(
        MarkdownBlockPeer parent,
        InlineImageRun run) =>
        _linkedImagePeerCache.GetValue(run, r => new MarkdownLinkedImagePeer(this, parent, r));

    internal bool TryGetLinkedImagePeer(
        InlineImageRun run,
        out MarkdownLinkedImagePeer peer)
    {
        if (_linkedImagePeerCache.TryGetValue(run, out MarkdownLinkedImagePeer? cached) &&
            cached is not null)
        {
            peer = cached;
            return true;
        }

        peer = null!;
        return false;
    }

    internal bool IsKeyboardFocusOnLink(LinkRun run)
    {
        return TryGetFocusedLink(out _, out var focusedRun) &&
               AreEquivalentLinks(focusedRun, run);
    }

    internal bool IsKeyboardFocusOnLinkedImage(InlineImageRun run) =>
        TryGetFocusedLinkedImage(out _, out var focusedRun) &&
        ReferenceEquals(focusedRun, run);

    internal bool IsKeyboardFocusOnVectorSemantic(
        Layout.Boxes.VectorSceneBox box,
        int semanticIndex) =>
        FocusState != FocusState.Unfocused &&
        TryGetFocusedVectorSemantic(out var focusedBox, out var focusedSemanticIndex) &&
        ReferenceEquals(focusedBox, box) &&
        focusedSemanticIndex == semanticIndex;

    internal bool IsKeyboardFocusOnHorizontalOverflow(IHorizontalOverflowBox overflow) =>
        FocusState != FocusState.Unfocused &&
        TryGetFocusedHorizontalOverflow(out var focusedOverflow) &&
        ReferenceEquals(focusedOverflow, overflow);

    internal bool IsHorizontalOverflowKeyboardFocusable(IHorizontalOverflowBox overflow) =>
        TryGetFocusableIndexForHorizontalOverflow(overflow, out _);

    internal bool HasKeyboardFocusOnVirtualChild =>
        FocusState != FocusState.Unfocused &&
        (TryGetFocusedLink(out _, out _) ||
         TryGetFocusedLinkedImage(out _, out _) ||
         TryGetFocusedVectorSemantic(out _, out _) ||
         TryGetFocusedHorizontalOverflow(out _));

    internal bool FocusLinkFromAutomation(LinkRun run)
    {
        if (!TryGetFocusableIndexForLink(run, out var index))
            return Focus(FocusState.Programmatic);

        _pendingLazyFocus = null;
        _focusedItemIndex = index;
        _focusResumeItemIndex = -1;
        ScrollFocusedItemIntoView();
        bool focused = Focus(FocusState.Programmatic);
        UpdateFocusRing();
        NotifyFocusedItemAutomation();
        return focused;
    }

    internal bool FocusLinkedImageFromAutomation(InlineImageRun run)
    {
        if (!TryGetFocusableIndexForLinkedImage(run, out var index))
            return Focus(FocusState.Programmatic);

        _pendingLazyFocus = null;
        _focusedItemIndex = index;
        _focusResumeItemIndex = -1;
        ScrollFocusedItemIntoView();
        bool focused = Focus(FocusState.Programmatic);
        UpdateFocusRing();
        NotifyFocusedItemAutomation();
        return focused;
    }

    internal bool FocusVectorSemanticFromAutomation(
        Layout.Boxes.VectorSceneBox box,
        int semanticIndex)
    {
        if (!TryGetFocusableIndexForVectorSemantic(box, semanticIndex, out int index))
            return false;

        _pendingLazyFocus = null;
        _focusedItemIndex = index;
        _focusResumeItemIndex = -1;
        ScrollFocusedItemIntoView();
        bool focused = Focus(FocusState.Programmatic);
        UpdateFocusRing();
        NotifyFocusedItemAutomation();
        return focused;
    }

    internal bool FocusHorizontalOverflowFromAutomation(IHorizontalOverflowBox overflow)
    {
        if (!TryGetFocusableIndexForHorizontalOverflow(overflow, out int index))
            return false;

        _pendingLazyFocus = null;
        _focusedItemIndex = index;
        _focusResumeItemIndex = -1;
        ScrollFocusedItemIntoView();
        bool focused = Focus(FocusState.Programmatic);
        UpdateFocusRing();
        NotifyFocusedItemAutomation();
        return focused;
    }

    internal bool FocusDocumentFromAutomation()
    {
        _suppressNextFocusPromotion = true;
        _pendingLazyFocus = null;
        _focusedItemIndex = -1;
        UpdateFocusRing();
        bool focused = Focus(FocusState.Programmatic);
        if (!focused)
            _suppressNextFocusPromotion = false;
        else
            DispatcherQueue.TryEnqueue(() => _suppressNextFocusPromotion = false);
        return focused;
    }

    /// <summary>Invoked by <see cref="MarkdownLinkPeer.Invoke"/> so UIA clients
    /// can activate a link the same way as pointer interaction. UIA callers
    /// can arrive on the RPC thread, so marshal back to the UI dispatcher
    /// before raising the public event.</summary>
    internal void RaiseLinkClickFromAutomation(LinkRun run)
    {
        if (run is null) return;
        DispatchAutomationLinkActivation(new LinkTarget(
            run.Url,
            run.Title,
            run.SourceSpan,
            run));
    }

    internal void RaiseLinkedImageClickFromAutomation(InlineImageRun run)
    {
        if (run is not { IsLinked: true, LinkUrl: { } url }) return;
        DispatchAutomationLinkActivation(new LinkTarget(
            url,
            run.LinkTitle,
            run.SourceSpan,
            null));
    }

    internal void SetDisclosureExpanded(LinkRun run, bool expanded)
    {
        if (run is not { IsDisclosure: true, DisclosureId: { Length: > 0 } id } ||
            run.IsExpanded == expanded)
        {
            return;
        }

        void Apply()
        {
            _disclosureStates[id] = expanded;
            DisclosureToggled?.Invoke(this, new MarkdownDisclosureToggledEventArgs(expanded));
            RequestRebuild(RebuildReason.Restyle);
        }

        if (DispatcherQueue is { HasThreadAccess: false } dispatcher)
        {
            dispatcher.TryEnqueue(Apply);
        }
        else
        {
            Apply();
        }
    }

    /// <summary>Raised when the user activates a non-internal markdown link.</summary>
    public event EventHandler<MarkdownLinkClickEventArgs>? LinkClick;

    /// <summary>Raised after a safe HTML disclosure is expanded or collapsed.</summary>
    public event EventHandler<MarkdownDisclosureToggledEventArgs>? DisclosureToggled;

    /// <summary>Raised after a selection or code block clipboard copy is attempted.</summary>
    public event EventHandler<MarkdownCopyCompletedEventArgs>? CopyCompleted;

    /// <summary>Raised when an image source is deliberately blocked or unavailable.</summary>
    public event EventHandler<MarkdownImageUnavailableEventArgs>? ImageUnavailable;

    /// <summary>Raised after the current Markdown document is rendered successfully.</summary>
    public event EventHandler? RenderCompleted;

    /// <summary>Raised when the current Markdown document cannot be rendered.</summary>
    public event EventHandler<MarkdownRenderFailedEventArgs>? RenderFailed;

    private void RaiseImageUnavailable(
        string source,
        MarkdownImageUnavailableReason reason,
        MarkdownSvgFailureReason? svgFailureReason)
    {
        if (_isDisposed || reason == MarkdownImageUnavailableReason.None)
            return;

        void Raise()
        {
            if (!_isDisposed)
                ImageUnavailable?.Invoke(this, new MarkdownImageUnavailableEventArgs(source, reason, svgFailureReason));
        }

        var dispatcher = DispatcherQueue;
        if (dispatcher is not null && !dispatcher.HasThreadAccess)
            dispatcher.TryEnqueue(Raise);
        else
            Raise();
    }

    /// <summary>
    /// Raised after every embed realisation pass (initial layout commit,
    /// scroll, resize). Subscribers can use it to surface
    /// <see cref="RealizedEmbedCount"/> for diagnostics or UI-automation
    /// tests without polluting the control's UIA surface.
    /// </summary>
    public event EventHandler? EmbedsRealizationChanged;

    /// <summary>
    /// Current vertical offset owned by this control. An ancestor-owned
    /// effective viewport is already represented by the control's transform to
    /// screen coordinates and must not be subtracted a second time.
    /// </summary>
    internal double CurrentScrollOffsetY => _scroll?.VerticalOffset ?? 0;

    internal bool OwnsScrollViewport => _ownsScrollViewport;

    /// <summary>
    /// Internal gate-harness signal used to ensure a warm scroll trace starts
    /// only after queued/background band realization has retired.
    /// </summary>
    internal bool HasPendingLazyLayoutWork =>
        _lazyLayoutDispatchQueued ||
        _hasPendingLazyLayoutBand ||
        _lazyLayoutTask is { IsCompleted: false };

    /// <summary>
    /// Returns a completion task for every snapshot retired before this call.
    /// Disposal failures from that drain epoch are surfaced once; later drains
    /// are not permanently poisoned by an earlier failed retirement.
    /// Production teardown remains nonblocking; the performance harness uses
    /// this internal seam to prevent disposal work from contaminating the next
    /// independent sample.
    /// </summary>
    internal Task DrainRetiredSnapshotsAsync()
        => _snapshotRetirements.DrainAsync();

    /// <summary>
    /// Releases device-bound snapshot resources before the performance harness
    /// deliberately replaces Win2D's shared device. Real device-loss HRESULTs
    /// are handled by the normal paint recovery path; this avoids a synthetic
    /// Dispose race producing an unrelated cross-factory E_FAIL.
    /// </summary>
    internal void PrepareForSyntheticDeviceReset()
    {
        CancelLazyLayoutRealization();
        LayoutSnapshot? snapshot = _snapshot;
        _snapshot = null;
        _snapshotPipelineStartTimestamp = 0;
        RetireSnapshot(snapshot);
    }

    /// <summary>Raises a host-approved vector-scene link/action from UI Automation.</summary>
    internal void RaiseVectorSceneLinkClickFromAutomation(
        Layout.Boxes.VectorSceneBox box,
        int semanticIndex)
    {
        if (box is null || !box.TryGetLink(semanticIndex, out var link) || link is null)
            return;

        var semantic = box.GetSemanticItem(semanticIndex);
        DispatchAutomationLinkActivation(new LinkTarget(
            link.Target ?? string.Empty,
            semantic?.Description,
            semantic?.SourceSpan ?? box.Content.SourceSpan,
            null,
            link.Action,
            link.External));
    }

    private void DispatchAutomationLinkActivation(LinkTarget target)
    {
        void Activate() => ActivateLinkTarget(
            target,
            MarkdownLinkInputKind.Automation,
            MarkdownInputModifiers.None);

        var dispatcher = DispatcherQueue;
        if (dispatcher is not null && !dispatcher.HasThreadAccess)
            dispatcher.TryEnqueue(Activate);
        else
            Activate();
    }

    internal bool TryGetAutomationScrollMetrics(
        out double verticalOffset,
        out double viewportHeight,
        out double extentHeight)
    {
        if (_scroll is null)
        {
            verticalOffset = 0;
            viewportHeight = 0;
            extentHeight = 0;
            return false;
        }

        verticalOffset = _scroll.VerticalOffset;
        viewportHeight = _scroll.ViewportHeight;
        extentHeight = _scroll.ExtentHeight;
        return true;
    }

    internal void ScrollFromAutomation(ScrollAmount amount)
    {
        if (_scroll is null || amount == ScrollAmount.NoAmount)
            return;

        double delta = amount switch
        {
            ScrollAmount.LargeDecrement => -Math.Max(48, _scroll.ViewportHeight * 0.8),
            ScrollAmount.SmallDecrement => -48,
            ScrollAmount.SmallIncrement => 48,
            ScrollAmount.LargeIncrement => Math.Max(48, _scroll.ViewportHeight * 0.8),
            _ => 0,
        };
        double maximum = Math.Max(0, _scroll.ExtentHeight - _scroll.ViewportHeight);
        double target = Math.Clamp(_scroll.VerticalOffset + delta, 0, maximum);
        _scroll.ChangeView(null, target, null, disableAnimation: true);
    }

    internal void SetAutomationVerticalScrollPercent(double percent)
    {
        if (_scroll is null)
            return;

        double maximum = Math.Max(0, _scroll.ExtentHeight - _scroll.ViewportHeight);
        double target = maximum * (Math.Clamp(percent, 0, 100) / 100);
        _scroll.ChangeView(null, target, null, disableAnimation: true);
    }

    /// <summary>
    /// Offset applied between the scroll viewport and document content. The
    /// renderer pins short documents to the top of the viewport, so this is
    /// normally zero.
    /// </summary>
    internal double CurrentContentOffsetY => 0;

    /// <inheritdoc />
    protected override AutomationPeer OnCreateAutomationPeer() => new MarkdownAutomationPeer(this);

    /// <summary>
    /// Internal accessor for the current layout snapshot. Used by the
    /// automation peer to walk the document structure (headings, links, etc.).
    /// May be null before the first layout completes.
    /// </summary>
    internal LayoutSnapshot? CurrentSnapshot => _snapshot;

    /// <summary>
    /// Last committed theme snapshot. Used by automation peers to expose UIA
    /// text attributes that match the pixels currently painted on the canvas.
    /// </summary>
    internal Theming.ThemeSnapshot? CurrentThemeSnapshot => _themeSnapshot;

    /// <summary>Effective document-language LCID exposed through UI Automation.</summary>
    internal int AutomationCultureLcid
    {
        get
        {
            return AutomationCulture.LCID;
        }
    }

    /// <summary>Culture resolved from the same language snapshot used by the committed layout.</summary>
    internal System.Globalization.CultureInfo AutomationCulture =>
        GetEffectiveLocalization().Culture;

    /// <summary>
    /// Number of currently-realised hosted embed elements (block + inline).
    /// Exposed for UI-automation tests that want to validate virtualisation
    /// without relying on heuristic descendant couits.
    /// </summary>
    public int RealizedEmbedCount
    {
        get
        {
            int n = 0;
            foreach (var p in _embedPlans) if (p.Realized is not null) n++;
            return n;
        }
    }

    private Button CreateCodeBlockCopyButton()
    {
        var button = new Button
        {
            Content = new SymbolIcon(Symbol.Copy),
            Style = CodeBlockCopyButtonStyle,
            Padding = new Thickness(0),
            MinWidth = 0,
            MinHeight = 0,
            IsTabStop = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetAutomationId(button, "MarkdownCodeBlockCopyButton");
        SetCodeBlockCopyButtonState(button, copied: false);
        return button;
    }

    private (Button Button, bool Attached) RentCodeBlockCopyButton()
    {
        if (_codeBlockCopyButtonPool.Count == 0)
            return (CreateCodeBlockCopyButton(), false);

        var pooled = _codeBlockCopyButtonPool.Pop();
        Button button = pooled.Button;
        if (!ReferenceEquals(button.Style, CodeBlockCopyButtonStyle))
            button.Style = CodeBlockCopyButtonStyle;
        return (button, pooled.Attached);
    }

    private void ReturnCodeBlockCopyButton(Button button)
    {
        bool keepAttached = !_derealizingAllEmbeds &&
                            _codeBlockCopyButtonPool.Count < CodeBlockCopyButtonPoolCapacity;
        button.Visibility = Visibility.Collapsed;
        if (_codeBlockCopyButtonPool.Count < CodeBlockCopyButtonPoolCapacity)
        {
            _codeBlockCopyButtonPool.Push((button, keepAttached));
            return;
        }

        try { _overlay?.Children.Remove(button); } catch { }
    }

    internal string ResolvedCodeBlockCopyButtonLabel =>
        string.IsNullOrWhiteSpace(CodeBlockCopyButtonLabel)
            ? ResolveLocalizedString(MarkdownStringKeys.CopyCode, MarkdownLocalizedStrings.CodeBlockCopyAutomationName)
            : CodeBlockCopyButtonLabel!;

    private string ResolvedCodeBlockCopiedButtonLabel =>
        string.IsNullOrWhiteSpace(CodeBlockCopiedButtonLabel)
            ? ResolveLocalizedString(MarkdownStringKeys.CodeCopied, MarkdownLocalizedStrings.CodeBlockCopied)
            : CodeBlockCopiedButtonLabel!;

    internal string ResolveLocalizedString(string key, string fallback)
    {
        MarkdownLocalizationSnapshot localization = GetEffectiveLocalization();
        return ResolveLocalizedString(key, fallback, localization);
    }

    private static string ResolveLocalizedString(
        string key,
        string fallback,
        MarkdownLocalizationSnapshot localization)
    {
        var culture = localization.Culture;
        string localizedFallback = MarkdownLocalizedStrings.Resolve(key, fallback, culture);
        try
        {
            string? value = localization.StringProvider?.GetString(key, culture);
            return string.IsNullOrWhiteSpace(value) ? localizedFallback : value;
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine($"[MarkdownRendererControl] String provider failed for '{key}': {ex.Message}");
            return localizedFallback;
        }
    }

    internal string ResolveFormattedLocalizedString(
        string key,
        string fallbackFormat,
        params object?[] arguments)
    {
        MarkdownLocalizationSnapshot localization = GetEffectiveLocalization();
        string format = ResolveLocalizedString(key, fallbackFormat, localization);
        try
        {
            return string.Format(localization.Culture, format, arguments);
        }
        catch (FormatException)
        {
            return string.Format(
                localization.Culture,
                fallbackFormat,
                arguments);
        }
    }

    private MarkdownLocalizationSnapshot GetEffectiveLocalization()
    {
        MarkdownLocalizationSnapshot? committed =
            MarkdownLocalizationSnapshot.SelectCommitted(
                _snapshot is not null,
                _committedLocalization);
        if (committed is not null)
            return committed;

        return CaptureRequestedLocalization();
    }

    private MarkdownLocalizationSnapshot CaptureRequestedLocalization() =>
        MarkdownLocalizationSnapshot.Capture(
            Language,
            _environmentSnapshot.Language,
            StringProvider,
            System.Globalization.CultureInfo.CurrentUICulture);

    private void OnStringProviderChanged()
    {
        if (_selectionStartHandle is not null)
            UpdateSelectionHandleLocalization(_selectionStartHandle, SelectionHandleEndpoint.Start);
        if (_selectionEndHandle is not null)
            UpdateSelectionHandleLocalization(_selectionEndHandle, SelectionHandleEndpoint.End);
        RequestRebuild(RebuildReason.Restyle);
    }

    private void SetCodeBlockCopyButtonState(Button button, bool copied)
    {
        string label = copied ? ResolvedCodeBlockCopiedButtonLabel : ResolvedCodeBlockCopyButtonLabel;
        if (button.Content is SymbolIcon icon)
            icon.Symbol = copied ? Symbol.Accept : Symbol.Copy;
        else
            button.Content = new SymbolIcon(copied ? Symbol.Accept : Symbol.Copy);

        AutomationProperties.SetName(button, label);
        ToolTipService.SetToolTip(button, label);
    }

    private void UpdateCodeBlockCopyButtonLabels()
    {
        foreach (var plan in _codeBlockActionPlans)
        {
            if (plan.Realized is Button button)
                SetCodeBlockCopyButtonState(button, copied: false);
        }
    }

    private void UpdateCodeBlockCopyButtonStyles()
    {
        foreach (var plan in _codeBlockActionPlans)
        {
            if (plan.Realized is Button button)
                button.Style = CodeBlockCopyButtonStyle;
        }
    }

    private void CopyCodeBlockToClipboard(CodeBlockActionPlan plan)
    {
        bool succeeded = false;
        try
        {
            var package = new DataPackage();
            package.SetText(plan.Box.CodeText);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            plan.ShowCopiedFeedback(this);
            succeeded = true;
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine($"[MarkdownRendererControl] Code block copy failed: {ex.Message}");
        }

        RaiseCopyCompleted(MarkdownCopyKind.CodeBlock, succeeded);
    }

    internal void CopyCodeBlockFromAutomation(Layout.Boxes.CodeBlockBox box)
    {
        foreach (CodeBlockActionPlan plan in _codeBlockActionPlans)
        {
            if (ReferenceEquals(plan.Box, box))
            {
                CopyCodeBlockToClipboard(plan);
                return;
            }
        }

        // UI Automation can invoke an off-screen code block before its
        // viewport-owned XAML button is realized. Copy directly without
        // realizing an overlay element or mutating the UIA child tree.
        CopyCodeBlockToClipboard(new CodeBlockActionPlan { Box = box });
    }

    private void RaiseCopyCompleted(MarkdownCopyKind kind, bool succeeded)
    {
        try
        {
            CopyCompleted?.Invoke(this, new MarkdownCopyCompletedEventArgs(kind, succeeded));
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine($"[MarkdownRendererControl] Copy completion subscriber failed: {ex.Message}");
        }
    }

    private void OnHostedEmbedKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element)
            return;

        bool shift = (Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down)
            == Windows.UI.Core.CoreVirtualKeyStates.Down;

        if (!TrySetFocusedItemForHostedElement(element)) return;

        if (e.Key == VirtualKey.Tab)
        {
            if (TryMoveFocusWithinHostedEmbed(element, e.OriginalSource, shift))
            {
                e.Handled = true;
                return;
            }

            e.Handled = MoveFocus(reverse: shift);
            return;
        }

        if (e.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down &&
            element is not TextBox)
        {
            e.Handled = MoveFocusSpatial(e.Key);
        }
    }

    /// <summary>
    /// The raw markdown source the renderer was last given. Useful for
    /// assistive technologies that want a flat textual representation when
    /// structural traversal isn't available.
    /// </summary>
    internal string CurrentMarkdownSource => Markdown ?? string.Empty;

    internal DocumentRange CurrentSelectionRange => _selection.Range;

    internal void SelectAutomationRange(DocumentRange range)
    {
        if (!IsSelectionEnabled) return;
        var normalized = range.Normalized();
        _selection.SetAnchor(normalized.Start);
        _selection.ExtendTo(normalized.End);
    }

    internal void ClearAutomationSelection() => _selection.Clear();

    internal bool TryGetVisibleDocumentRect(out Windows.Foundation.Rect rect)
    {
        bool hasViewport = TryGetViewport(out double top, out double height, out double width);
        rect = new Windows.Foundation.Rect(0, top, width, height);
        return hasViewport;
    }

    internal bool HasVisibleLoadingImagesForAutomation()
    {
        if (_imagePlans.Count == 0)
            return false;

        bool hasViewport = TryGetVisibleDocumentRect(out Windows.Foundation.Rect viewport);
        ViewportBandIndex? imageIndex = hasViewport ? _imagePlanIndex : null;
        ViewportRange range = imageIndex is not null
            ? imageIndex.Find(viewport.Top, viewport.Bottom)
            : new ViewportRange(0, _imagePlans.Count);
        for (int index = range.Start; index < range.End; index++)
        {
            Layout.Boxes.ImageBox image = _imagePlans[
                imageIndex is not null ? imageIndex.GetBlockOrdinal(index) : index];
            if (image.AccessibilityState != MarkdownImageAccessibilityState.Loading)
                continue;

            if (!hasViewport ||
                (image.Bounds.Width > 0 &&
                 image.Bounds.Height > 0 &&
                 image.Bounds.Right > viewport.Left &&
                 image.Bounds.Left < viewport.Right &&
                 image.Bounds.Bottom > viewport.Top &&
                 image.Bounds.Top < viewport.Bottom))
            {
                return true;
            }
        }

        return false;
    }

    internal void ScrollDocumentRectIntoView(Windows.Foundation.Rect rect, bool alignToTop)
    {
        if (_scroll is null)
        {
            BringDocumentRectIntoExternalViewport(rect, animate: false, alignToTop ? 0 : 0.5);
            return;
        }
        const double margin = 24.0;
        double target = alignToTop
            ? rect.Top
            : rect.Top < _scroll.VerticalOffset + margin
                ? rect.Top - margin
                : rect.Bottom > _scroll.VerticalOffset + _scroll.ViewportHeight - margin
                    ? rect.Bottom - _scroll.ViewportHeight + margin
                    : _scroll.VerticalOffset;
        // UI Automation's ScrollIntoView contract is immediate. An animated
        // change can be superseded by lazy-layout realization before its first
        // frame, leaving the requested range offscreen.
        if (ShakeLogger.IsEnabled)
        {
            ShakeLogger.Log(
                "uia-scroll-request",
                $"rect={rect.X:F2},{rect.Y:F2},{rect.Width:F2},{rect.Height:F2} " +
                $"alignTop={alignToTop} current={_scroll.VerticalOffset:F2} target={Math.Max(0, target):F2} " +
                $"viewport={_scroll.ViewportHeight:F2} extent={_scroll.ExtentHeight:F2}");
        }
        _scroll.ChangeView(null, Math.Max(0, target), null, disableAnimation: true);
    }

    /// <summary>Initializes a new markdown renderer control.</summary>
    public MarkdownRendererControl()
        : this(ownsScrollViewport: true)
    {
    }

    /// <summary>
    /// Initializes a renderer with either an owned scroll viewport or an
    /// externally supplied effective viewport.
    /// </summary>
    /// <param name="ownsScrollViewport">
    /// True to create the compatibility scroll viewer; false to participate in
    /// the nearest ancestor viewport through EffectiveViewportChanged.
    /// </param>
    protected MarkdownRendererControl(bool ownsScrollViewport)
    {
        _ownsScrollViewport = ownsScrollViewport;
        _canvasPointerMovedHandler = OnPointerMoved;
        _canvasPointerReleasedHandler = OnPointerReleased;
        _canvasPointerCanceledHandler = OnPointerCanceledOrCaptureLost;
        _canvasPointerCaptureLostHandler = OnPointerCanceledOrCaptureLost;
        IsTabStop = true;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Loaded += (_, _) => OnLoadedInternal();
        Unloaded += (_, _) => OnUnloaded();
        ActualThemeChanged += (_, _) => OnThemeChanged();
        // Selection changes update the XAML overlay (not the DirectWrite canvas),
        // so tiles are never invalidated during a drag.
        _selection.Changed += (_, _) => OnSelectionChanged();
        // FlowDirection has no DP-changed callback we can register at the
        // class level (it's defined by FrameworkElement). Listen for live
        // changes via the dependency-property-changed callback API so RTL
        // toggling at runtime rebuilds the layout instead of leaving stale
        // CanvasTextLayouts in the previous direction. Gate on IsLoaded so
        // the inheritance pass during initial visual-tree attach doesn't
        // race against the Loaded handler's own RequestRebuild.
        RegisterPropertyChangedCallback(FlowDirectionProperty, (_, _) =>
        {
            if (IsLoaded) RequestRebuild();
        });
        RegisterPropertyChangedCallback(LanguageProperty, (_, _) =>
        {
            if (IsLoaded) RequestRebuild(RebuildReason.Restyle);
        });
        BuildVisualTree();
    }

    private void BuildVisualTree()
    {
        if (_ownsScrollViewport)
        {
            _scroll = new ScrollViewer
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalScrollMode = ScrollMode.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                VerticalScrollMode = ScrollMode.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalContentAlignment = VerticalAlignment.Top,
                ZoomMode = ZoomMode.Disabled,
            };
        }
        _root = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
        };
        _overlay = new Canvas
        {
            // Background null so the empty overlay doesn't capture pointer
            // events. Hosted embeds are individually hit-testable.
            Background = null,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsHitTestVisible = true,
            VerticalAlignment = VerticalAlignment.Top,
        };
        _root.Children.Add(_overlay);
        // Selection, hover and drag chrome is created only when used. A new
        // document does not need an Image, Border and two Button handles in
        // its XAML tree merely to paint its first viewport.
        if (_scroll is not null)
            _scroll.Content = _root;

        EnsureCanvas();
        GettingFocus += OnGettingFocus;
        GotFocus += OnGotFocus;
        LosingFocus += OnLosingFocus;
        KeyDown += OnKeyDown;

        if (_scroll is not null)
        {
            Content = _scroll;
        }
        else
        {
            EffectiveViewportChanged += OnEffectiveViewportChanged;
            Content = _root;
        }
    }

    private void EnsureCanvas()
    {
        if (_canvas is not null || _root is null || _isDisposed)
            return;
        _canvas = new CanvasVirtualControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsDoubleTapEnabled = true,
            IsHoldingEnabled = true,
            IsTapEnabled = true,
            VerticalAlignment = VerticalAlignment.Top,
        };
        _root.Children.Insert(0, _canvas);
        _canvas.PointerPressed += OnPointerPressed;
        _canvas.PointerWheelChanged += OnPointerWheelChanged;
        _canvas.PointerExited += OnPointerExited;
        _canvas.Tapped += OnTapped;
        _canvas.DoubleTapped += OnDoubleTapped;
        _canvas.Holding += OnHolding;
        _canvas.RightTapped += OnRightTapped;
        _canvas.AddHandler(UIElement.PointerMovedEvent, _canvasPointerMovedHandler, handledEventsToo: true);
        _canvas.AddHandler(UIElement.PointerReleasedEvent, _canvasPointerReleasedHandler, handledEventsToo: true);
        _canvas.AddHandler(UIElement.PointerCanceledEvent, _canvasPointerCanceledHandler, handledEventsToo: true);
        _canvas.AddHandler(UIElement.PointerCaptureLostEvent, _canvasPointerCaptureLostHandler, handledEventsToo: true);
        AttachCanvasRenderHandlers();
    }

    private void OnEffectiveViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs args)
    {
        if (_ownsScrollViewport || _isDisposed)
            return;

        Rect viewport = args.EffectiveViewport;
        if (!double.IsFinite(viewport.X) || !double.IsFinite(viewport.Y) ||
            !double.IsFinite(viewport.Width) || !double.IsFinite(viewport.Height) ||
            viewport.Width <= 0 || viewport.Height <= 0)
        {
            return;
        }

        bool materiallyChanged = !_hasExternalViewport ||
            Math.Abs(_externalViewport.X - viewport.X) >= 0.5 ||
            Math.Abs(_externalViewport.Y - viewport.Y) >= 0.5 ||
            Math.Abs(_externalViewport.Width - viewport.Width) >= 0.5 ||
            Math.Abs(_externalViewport.Height - viewport.Height) >= 0.5;
        _externalViewport = viewport;
        _hasExternalViewport = true;
        if (!materiallyChanged || _isUnloaded)
            return;

        QueueLazyLayoutForViewport();
        UpdateSelectionAdornerViewport();
        InvalidateSelectionAdorner();
        RealizeVisibleEmbeds();
        ScheduleVisibleCodeBlockHighlighting();
    }

    private bool TryGetViewport(out double top, out double height, out double width)
    {
        if (_scroll is not null)
        {
            top = _scroll.VerticalOffset;
            height = _scroll.ViewportHeight > 0 ? _scroll.ViewportHeight : Math.Max(ActualHeight, 1);
            width = _scroll.ViewportWidth > 0 ? _scroll.ViewportWidth : Math.Max(ActualWidth, 1);
            return true;
        }

        if (_hasExternalViewport)
        {
            top = Math.Max(0, _externalViewport.Top);
            height = Math.Max(1, _externalViewport.Height);
            width = Math.Max(1, _externalViewport.Width);
            return true;
        }

        top = 0;
        height = Math.Max(ActualHeight, 600);
        width = Math.Max(ActualWidth, 1);
        return false;
    }

    private bool BringDocumentRectIntoExternalViewport(Rect rect, bool animate, double alignmentRatio = 0)
    {
        if (_ownsScrollViewport || _root is null)
            return false;

        try
        {
            _root.StartBringIntoView(new BringIntoViewOptions
            {
                AnimationDesired = animate,
                TargetRect = rect,
                VerticalAlignmentRatio = Math.Clamp(alignmentRatio, 0, 1),
                VerticalOffset = 0,
            });
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void OnGettingFocus(UIElement sender, GettingFocusEventArgs e)
    {
        if (!ReferenceEquals(e.NewFocusedElement, this))
            return;

        var direction = e.Direction;
        _lastFocusEntryWasKeyboardInput = e.InputDevice == FocusInputDeviceKind.Keyboard;
        _lastFocusEntryWasKeyboardTraversal =
            direction is Microsoft.UI.Xaml.Input.FocusNavigationDirection.Next
                      or Microsoft.UI.Xaml.Input.FocusNavigationDirection.Previous;
        _lastFocusEntryWasReverse = direction == Microsoft.UI.Xaml.Input.FocusNavigationDirection.Previous;
    }

    private void OnGotFocus(object sender, RoutedEventArgs e)
    {
        if (_promotingKeyboardFocusEntry || _isUnloaded)
            return;

        if (!ReferenceEquals(e.OriginalSource, this) || FocusState == FocusState.Pointer)
            return;

        if (_suppressNextFocusPromotion)
        {
            _suppressNextFocusPromotion = false;
            _lastFocusEntryWasKeyboardTraversal = false;
            _lastFocusEntryWasKeyboardInput = false;
            return;
        }

        if (!_lastFocusEntryWasKeyboardTraversal &&
            !_lastFocusEntryWasKeyboardInput &&
            FocusState != FocusState.Keyboard)
            return;

        if (_pendingLazyFocus is not null)
        {
            HideFocusRing();
            _lastFocusEntryWasKeyboardTraversal = false;
            _lastFocusEntryWasKeyboardInput = false;
            return;
        }

        if (_focusedItemIndex >= 0)
        {
            UpdateFocusRing();
            NotifyFocusedItemAutomation();
            return;
        }

        _promotingKeyboardFocusEntry = true;
        try
        {
            MoveFocus(_lastFocusEntryWasReverse);
        }
        finally
        {
            _promotingKeyboardFocusEntry = false;
            _lastFocusEntryWasKeyboardTraversal = false;
            _lastFocusEntryWasKeyboardInput = false;
        }
    }

    private void OnLosingFocus(UIElement sender, LosingFocusEventArgs e)
    {
        _suppressNextFocusPromotion = false;

        if (e.NewFocusedElement is DependencyObject next && IsElementWithinRenderer(next))
            return;

        _pendingLazyFocus = null;
        _focusedItemIndex = -1;
        UpdateFocusRing();
        if (!_contextMenuOpen)
        {
            _selectionAnchor = null;
            _clickMode = ClickMode.Single;
        }
    }

    private void OnMarkdownChanged()
    {
        // Document has documented precedence over Markdown. A source change
        // invalidates only the document that this control published itself;
        // it must never clear a host-supplied/bound document based on DP
        // notification order. Hosts switch back to source parsing by clearing
        // Document explicitly.
        if (!_hasExplicitDocument && !_isUpdatingDocumentProperty && Document is not null)
        {
            _isUpdatingDocumentProperty = true;
            try { ClearValue(DocumentProperty); }
            finally { _isUpdatingDocumentProperty = false; }
        }
        _disclosureStates.Clear();
        _hostedElementFallbacks.Clear();
        ClearCodeBlockHighlightCache();
        RequestRebuild();
    }

    private void OnDocumentChanged(DependencyPropertyChangedEventArgs e)
    {
        if (_isUpdatingDocumentProperty)
            return;

        _hasExplicitDocument = e.NewValue is MarkdownRenderer.Document.MarkdownDocument;
        _disclosureStates.Clear();
        _hostedElementFallbacks.Clear();
        ClearCodeBlockHighlightCache();
        RequestRebuild();
    }

    private void OnEngineChanged(DependencyPropertyChangedEventArgs e)
    {
        if (_ownedEngine is not null &&
            ReferenceEquals(e.OldValue, _ownedEngine) &&
            !ReferenceEquals(e.NewValue, _ownedEngine))
        {
            MarkdownEngine owned = _ownedEngine;
            _ownedEngine = null;
            owned.Dispose();
        }

        OnParserConfigurationChanged();
    }

    private void OnParserConfigurationChanged()
    {
        // Document is both an input and the published output of Markdown
        // parsing. Only a document supplied by the host is authoritative.
        // Clear an internally published value so changing Engine or the legacy
        // registry cannot silently reuse an AST produced by old settings.
        if (!_hasExplicitDocument && Document is not null)
        {
            _isUpdatingDocumentProperty = true;
            try { ClearValue(DocumentProperty); }
            finally { _isUpdatingDocumentProperty = false; }
        }

        _disclosureStates.Clear();
        _hostedElementFallbacks.Clear();
        ClearCodeBlockHighlightCache();
        RequestRebuild();
    }

    private void OnHostedElementFactoryChanged()
    {
        _hostedElementFallbacks.Clear();
        RequestRebuild();
    }

    private void OnLegacyCodeHighlighterChanged(DependencyPropertyChangedEventArgs e)
    {
        if (_synchronizingCodeHighlighter)
            return;

        ICodeHighlighter? previous = CodeHighlighter;
        _synchronizingCodeHighlighter = true;
        try
        {
#pragma warning disable CS0618 // Synchronizes the preview compatibility alias.
            if (e.NewValue is null)
            {
                _legacyCodeHighlighterAdapter = null;
                CodeHighlighter = null;
            }
            else if (e.NewValue is ICodeHighlighter stable)
            {
                _legacyCodeHighlighterAdapter = null;
                CodeHighlighter = stable;
            }
            else if (e.NewValue is ICodeBlockSyntaxHighlighter legacy)
            {
                if (_legacyCodeHighlighterAdapter is null ||
                    !ReferenceEquals(_legacyCodeHighlighterAdapter.Inner, legacy))
                {
                    _legacyCodeHighlighterAdapter = new LegacyCodeHighlighterAdapter(legacy);
                }

                CodeHighlighter = _legacyCodeHighlighterAdapter;
            }
#pragma warning restore CS0618
        }
        finally
        {
            _synchronizingCodeHighlighter = false;
        }

        OnEffectiveCodeHighlighterChanged(previous, CodeHighlighter);
    }

    private void OnCodeHighlighterChanged(DependencyPropertyChangedEventArgs e)
    {
        if (_synchronizingCodeHighlighter)
            return;

        _synchronizingCodeHighlighter = true;
        try
        {
            _legacyCodeHighlighterAdapter = e.NewValue as LegacyCodeHighlighterAdapter;
#pragma warning disable CS0618 // Keep the compatibility alias bidirectionally synchronized.
            CodeBlockSyntaxHighlighter = e.NewValue as ICodeBlockSyntaxHighlighter;
#pragma warning restore CS0618
        }
        finally
        {
            _synchronizingCodeHighlighter = false;
        }

        OnEffectiveCodeHighlighterChanged(
            e.OldValue as ICodeHighlighter,
            e.NewValue as ICodeHighlighter);
    }

    private void OnEffectiveCodeHighlighterChanged(
        ICodeHighlighter? previous,
        ICodeHighlighter? current)
    {
        if (ReferenceEquals(previous, current))
            return;

        IDisposable? replacedOwner = null;
        if (_ownedCodeHighlighterService is not null &&
            ReferenceEquals(previous, _ownedCodeHighlighterService))
        {
            replacedOwner = _ownedCodeHighlighter;
            _ownedCodeHighlighter = null;
            _ownedCodeHighlighterService = null;
        }

        Task retirement = RetireCodeBlockHighlighting(restart: !_isUnloaded && !_isDisposed);
        if (replacedOwner is not null)
            RetireOwnedCodeHighlighter(replacedOwner, retirement);
        ClearCodeBlockHighlightCache();
        RequestRebuild();
    }

    private bool IsElementWithinRenderer(DependencyObject element)
        => IsElementWithin(element, this);

    private bool IsElementWithinCodeBlockAction(DependencyObject element)
    {
        foreach (var plan in _codeBlockActionPlans)
        {
            if (plan.Realized is DependencyObject realized && IsElementWithin(element, realized))
                return true;
        }

        return false;
    }

    private static bool IsElementWithin(DependencyObject element, DependencyObject ancestor)
    {
        for (DependencyObject? current = element; current is not null;)
        {
            if (ReferenceEquals(current, ancestor))
                return true;

            try { current = VisualTreeHelper.GetParent(current); }
            catch { return false; }
        }

        return false;
    }

    private void RegisterSelectionDismissalHook()
    {
        if (_selectionDismissalRoot is not null)
            return;

        var root = XamlRoot?.Content as UIElement;
        if (root is null)
            return;

        _selectionDismissalPointerPressedHandler = OnAppRootPointerPressed;
        root.AddHandler(UIElement.PointerPressedEvent, _selectionDismissalPointerPressedHandler, handledEventsToo: true);
        _selectionCopyKeyDownHandler = OnAppRootKeyDown;
        root.AddHandler(UIElement.KeyDownEvent, _selectionCopyKeyDownHandler, handledEventsToo: true);
        _selectionDismissalRoot = root;
    }

    private void UnregisterSelectionDismissalHook()
    {
        if (_selectionDismissalRoot is not null && _selectionDismissalPointerPressedHandler is not null)
        {
            try { _selectionDismissalRoot.RemoveHandler(UIElement.PointerPressedEvent, _selectionDismissalPointerPressedHandler); }
            catch { }
        }

        if (_selectionDismissalRoot is not null && _selectionCopyKeyDownHandler is not null)
        {
            try { _selectionDismissalRoot.RemoveHandler(UIElement.KeyDownEvent, _selectionCopyKeyDownHandler); }
            catch { }
        }

        _selectionDismissalRoot = null;
        _selectionDismissalPointerPressedHandler = null;
        _selectionCopyKeyDownHandler = null;
    }

    private void OnAppRootPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_isUnloaded)
            return;

        var point = e.GetCurrentPoint(sender as UIElement ?? this);
        var properties = point.Properties;
        if (properties.IsRightButtonPressed || properties.IsMiddleButtonPressed || properties.IsXButton1Pressed || properties.IsXButton2Pressed)
            return;

        if (e.OriginalSource is DependencyObject source && IsElementWithinRenderer(source))
        {
            MarkdownSelectionCoordinator.ClearSelectionsExcept(this);
            if (IsElementWithinCodeBlockAction(source))
                return;

            bool overCanvas = IsPointerOverCanvasTextSurface(e, out var canvasPoint);
            bool overEmbed = overCanvas && IsPointOverEmbed(canvasPoint);
            if (!overCanvas || overEmbed)
                ClearSelectionForExternalInteraction();
            return;
        }

        ClearSelectionForExternalInteraction();
    }

    private void OnAppRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_isUnloaded || _snapshot is null || !_selection.IsActive)
            return;

        bool ctrl = (Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down)
            == Windows.UI.Core.CoreVirtualKeyStates.Down;
        if (!ctrl || e.Key != VirtualKey.C)
            return;

        if (ExecuteSelectionCommand(MarkdownCommandKind.CopyRendered))
            e.Handled = true;
    }

    /// <summary>
    /// Copies the active selection to the clipboard.
    /// </summary>
    /// <param name="options">Optional copy format options. Defaults use rendered plain text and add CF_HTML.</param>
    /// <returns>True when a selection was copied successfully.</returns>
    public bool CopySelectionToClipboard(MarkdownCopyOptions? options = null)
    {
        var snapshot = _snapshot;
        if (snapshot is null || !_selection.IsActive)
            return false;

        bool succeeded;
        try
        {
            var effectiveOptions = options ?? MarkdownCopyOptions.Default;
            string? renderedText = null;
            string? renderedHtml = null;
            if (effectiveOptions.PlainTextMode == MarkdownPlainTextCopyMode.RenderedText ||
                effectiveOptions.IncludeHtml)
            {
                var rendered = GetRenderedSelectionPayload(snapshot, _selection.Range);
                if (effectiveOptions.PlainTextMode == MarkdownPlainTextCopyMode.RenderedText)
                    renderedText = rendered.Text;
                if (effectiveOptions.IncludeHtml)
                    renderedHtml = rendered.Html;
            }

            succeeded = MarkdownClipboardWriter.Copy(
                snapshot.SourceMap,
                _selection.Range,
                effectiveOptions,
                renderedText,
                renderedHtml);
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine($"[MarkdownRendererControl] Selection copy failed: {ex.Message}");
            succeeded = false;
        }

        RaiseCopyCompleted(MarkdownCopyKind.Selection, succeeded);
        return succeeded;
    }

    /// <summary>Copies the active selection as exact markdown source plus CF_HTML.</summary>
    public bool CopySelectionAsMarkdown()
        => CopySelectionToClipboard(new MarkdownCopyOptions
        {
            PlainTextMode = MarkdownPlainTextCopyMode.SourceMarkdown,
            IncludeHtml = true,
        });

    private bool ExecuteSelectionCommand(MarkdownCommandKind kind)
    {
        try
        {
            if (TryResolveSelectionCommand(kind, out var command, out var context))
            {
                if (!command.CanExecute(context))
                    return false;

                command.Execute(context);
                return true;
            }

            return kind == MarkdownCommandKind.CopyMarkdown
                ? CopySelectionAsMarkdown()
                : CopySelectionToClipboard();
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] Host command '{kind}' failed: {ex.Message}");
            return false;
        }
    }

    private bool CanExecuteSelectionCommandOrDefault(MarkdownCommandKind kind)
    {
        try
        {
            return !TryResolveSelectionCommand(kind, out var command, out var context) ||
                   command.CanExecute(context);
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] Host command '{kind}' query failed: {ex.Message}");
            return false;
        }
    }

    private bool TryResolveSelectionCommand(
        MarkdownCommandKind kind,
        out System.Windows.Input.ICommand command,
        out MarkdownCommandContext context)
    {
        SourceSpan sourceRange = SourceSpan.Empty;
        if (_snapshot is { } snapshot && _selection.IsActive)
            snapshot.SourceMap.TryMapRange(_selection.Range, out sourceRange);

        context = new MarkdownCommandContext(kind, sourceRange);
        command = CommandProvider?.GetCommand(context)!;
        return command is not null;
    }

    private static RenderedSelectionPayload GetRenderedSelectionPayload(
        LayoutSnapshot snapshot,
        DocumentRange range)
    {
        var semantic = snapshot.SemanticDocument;
        var normalized = range.Normalized();
        int start = semantic.TextOffsetFromDocumentPosition(normalized.Start);
        int end = semantic.TextOffsetFromDocumentPosition(normalized.End);
        if (end < start)
            (start, end) = (end, start);

        start = Math.Clamp(start, 0, semantic.Text.Length);
        end = Math.Clamp(end, start, semantic.Text.Length);
        return new RenderedSelectionPayload(
            semantic.Text.Substring(start, end - start),
            MarkdownRenderedHtmlWriter.BuildFragment(semantic, start, end));
    }

    private readonly record struct RenderedSelectionPayload(string Text, string Html);

    internal void ClearSelectionFromCoordinator()
    {
        ClearSelectionForExternalInteraction();
    }

    private void ClearSelectionForExternalInteraction(bool resetClickTracking = true)
    {
        CloseSelectionContextMenu();
        ResetDeferredPointerInput();
        _pointerSession = default;
        ResetSelectionHandleDrag();
        _touchSelection.Reset();
        _selectionAnchor = null;
        _clickMode = ClickMode.Single;
        SetSelectionDragShieldActive(false);
        if (resetClickTracking)
        {
            _consecutiveClickCount = 0;
            _lastPressTickMs = 0;
            _lastPressPoint = default;
        }

        if (_selection.IsActive || !_selection.Range.IsEmpty)
            _selection.Clear();

        if (_focusedItemIndex >= 0)
        {
            _pendingLazyFocus = null;
            _focusedItemIndex = -1;
            UpdateFocusRing();
        }
    }

    private bool IsPointerOverCanvasTextSurface(PointerRoutedEventArgs e, out Point canvasPoint)
    {
        canvasPoint = default;
        if (_canvas is null)
            return false;

        try
        {
            canvasPoint = e.GetCurrentPoint(_canvas).Position;
            return canvasPoint.X >= 0 &&
                   canvasPoint.Y >= 0 &&
                   canvasPoint.X <= _canvas.ActualWidth &&
                   canvasPoint.Y <= _canvas.ActualHeight;
        }
        catch
        {
            canvasPoint = default;
            return false;
        }
    }

    private void OnSelectionChanged()
    {
        if (_selection.IsActive)
            MarkdownSelectionCoordinator.ClearSelectionsExcept(this);

        UpdateSelectionOverlay();
        QueueSelectionAutomationEvent();
    }

    private void QueueSelectionAutomationEvent()
    {
        if (_selectionAutomationEventQueued || _isDisposed || _isUnloaded)
            return;

        _selectionAutomationEventQueued = true;
        var dispatcher = DispatcherQueue;
        if (dispatcher is null || !dispatcher.TryEnqueue(() =>
            {
                _selectionAutomationEventQueued = false;
                if (_isDisposed || _isUnloaded)
                    return;

                (FrameworkElementAutomationPeer.FromElement(this) as MarkdownAutomationPeer)?
                    .NotifySelectionChanged();
            }))
        {
            _selectionAutomationEventQueued = false;
        }
    }

    private void OnSelectionEnabledChanged(bool isEnabled)
    {
        if (!isEnabled)
            ClearSelectionForExternalInteraction();

        // TextPattern remains available for document reading, but its
        // SupportedTextSelection value changes between Single and None.
        // Recreate the peer-facing semantic snapshot and refresh cursor state.
        RequestRebuild();
        InvalidateAutomationLayout();
        SetCursorShape(null);
    }

    private void OnLoadedInternal()
    {
        if (_isDisposed)
            return;

        _isUnloaded = false;
        // RemoveFromVisualTree physically removes the Win2D child from _root.
        // Recreate it for a retained view; the old native child and its input
        // delegates must not retain the unloaded view through a WinRT cycle.
        EnsureCanvas();
        AttachWindowLifecycle();
        AttachCanvasRenderHandlers();
        if (_imageLifetimeCts is null || _imageLifetimeCts.IsCancellationRequested)
        {
            _imageLifetimeCts?.Dispose();
            _imageLifetimeCts = new CancellationTokenSource();
        }
        MarkdownSelectionCoordinator.Register(this);
        RegisterSelectionDismissalHook();
        _environmentSubscription?.Dispose();
        _environmentSubscription = MarkdownEnvironmentMonitor.Acquire(this, this);
        _environmentSnapshot = _environmentSubscription?.Snapshot ?? default;
        _sizeChangedHandler = (_, e) =>
        {
            if (Math.Abs(_lastWidth - (float)e.NewSize.Width) > 0.5f) RequestRebuild(RebuildReason.Restyle);
        };
        SizeChanged += _sizeChangedHandler;
        // Re-subscribe to Theme.Changed: OnUnloaded unhooks the handler, and
        // a Load→Unload→Load cycle (TabView reuse, navigation hide/show)
        // does not necessarily reassign the Theme DP, so without this the
        // control would silently stop reacting to theme.Invalidate() after
        // re-attach.  Unsubscribe first to avoid a duplicate handler if
        // Loaded fires before any Unload has occurred.
        if (Theme is { } t)
        {
            t.Changed -= OnThemeRevisionChanged;
            t.Changed += OnThemeRevisionChanged;
        }
        if (SvgRenderer is { } svgRenderer)
        {
            svgRenderer.CacheInvalidated -= OnSvgRendererCacheInvalidated;
            Volatile.Write(ref _subscribedSvgRenderer, svgRenderer);
            svgRenderer.CacheInvalidated += OnSvgRendererCacheInvalidated;
        }
        RequestRebuild();
    }

    private void OnUnloaded()
    {
        if (_isUnloaded)
            return;

        // Set before any unsubscription so dispatcher-queued lambdas
        // (e.g. from OnImageLoadCompleted) that are already in-flight know
        // not to call RequestRebuild after we've torn down.
        _isUnloaded = true;
        ResetDeferredPointerInput();
        // An unloaded view is reusable and no longer belongs to this window.
        // Do not let a former host destroy it, or retain weak-event wrappers
        // for every virtualized view until the window eventually closes.
        DetachWindowLifecycle();
        // Win2D can retain queued native draw/resource callbacks after XAML starts
        // removing this control. Stop those callbacks before releasing any snapshot
        // or DirectWrite resources they may reference.
        DetachCanvasRenderHandlers();
        DetachCanvasInputHandlers();
        // Win2D requires RemoveFromVisualTree from the containing control's
        // Unloaded boundary. At this point XAML has begun detaching the renderer,
        // so each Win2D child can unregister its XamlRoot callback without racing a
        // subsequent window resize.
        _canvas?.RemoveFromVisualTree();
        _canvas = null;
        ReleaseLoadedResources();
    }

    private void ReleaseLoadedResources()
    {
        CloseSelectionContextMenu();
        ClearHorizontalOverflowPointerState();
        HideAbbreviationTooltip();
        ReleaseAbbreviationTooltipTimers();
        if (_sizeChangedHandler is not null)
        {
            SizeChanged -= _sizeChangedHandler;
            _sizeChangedHandler = null;
        }
        if (Theme is { } t)
        {
            t.Changed -= OnThemeRevisionChanged;
        }
        if (Interlocked.Exchange(ref _subscribedSvgRenderer, null) is { } svgRenderer)
            svgRenderer.CacheInvalidated -= OnSvgRendererCacheInvalidated;
        _environmentSubscription?.Dispose();
        _environmentSubscription = null;
        UnregisterSelectionDismissalHook();
        MarkdownSelectionCoordinator.Unregister(this);
        var oldCts = _pipelineCts;
        var oldTask = _pipelineTask;
        _pipelineCts = null;
        _pipelineTask = null;
        _activePipelineGeneration = 0;
        RetireCancellationTokenSource(oldCts, oldTask);
        CancelLazyLayoutRealization();
        var oldImageLifetimeCts = _imageLifetimeCts;
        _imageLifetimeCts = null;
        RetireCancellationTokenSource(oldImageLifetimeCts, lifetime: null);
        _rebuildDispatchState = default;
        _hasPendingRebuild = false;
        _pendingRebuildReason = RebuildReason.Restyle;
        _imageRelayoutQueued = false;
        _imageRelayoutActive = false;
        RetireCancellationTokenSource(_imageRelayoutCts, _imageRelayoutTask);
        _imageRelayoutCts = null;
        _imageRelayoutTask = null;
        _lastLazyViewportTimestamp = 0;
        _lazyScrollVelocityPixelsPerSecond = 0;
        _pendingImageRelayoutBlockIndices.Clear();
        _selectionAutomationEventQueued = false;
        _horizontalOverflowAutomationEventQueued = false;
        _pendingHorizontalOverflowAutomationChanges.Clear();
        RetireCodeBlockHighlighting(restart: false);
        // Unsubscribe scroll handler so scroll-inertia events after visual-tree
        // removal don't fire OnScrollViewChanged on a partially-torn-down control.
        if (_scroll is not null) _scroll.ViewChanged -= OnScrollViewChanged;
        // Unsubscribe all in-flight image load handlers before clearing the plan
        // list; otherwise an async load completing after unload fires
        // OnImageLoadCompleted → RequestRebuild on a torn-down control, causing a
        // zombie rebuild cycle that re-subscribes handlers indefinitely.
        UnsubscribeAllImages();
        // Tear down embed plans before clearing the overlay so block embed
        // factories get RecycleBlock callbacks and inline embeds release
        // their Run.RealizedElement references — otherwise hosted controls
        // and their event handlers would leak past detach.
        DerealizeAllEmbeds();
        _embedPlans.Clear();
        _codeBlockActionPlans.Clear();
        _codeBlockCopyButtonPool.Clear();
        _imagePlans.Clear();
        _imagePlanIndex = null;
        _prefetchedMarkdownSource = null;
        _activeImagePrefetcher = null;
        _activeImagePrefetchContext = null;
        _prefetchedImageRegistryRevision = -1;
        _pendingImagePrefetch = null;
        _performanceDocumentScope?.Dispose();
        _performanceDocumentScope = null;
        _performanceDocumentSource = null;
        _performanceRegistryRevision = -1;
        _performancePrefetchStarted = false;
        _embedRects.Clear();
        _blockEmbedRects.Clear();
        ResetSelectionHandleDrag();
        _touchSelection.Reset();
        SetSelectionDragShieldActive(false);
        // Release native resources & hosted embeds so re-attaching the
        // control to a new visual parent doesn't leak DirectWrite layouts
        // or keep stale FrameworkElements alive.
        ReleaseSelectionAdornerSurface();
        _overlay?.Children.Clear();
        // Release ProtectedCursor before disposing the native cursor objects so the
        // XAML compositor doesn't access a dangling handle during the same render frame.
        ProtectedCursor = null;
        _currentCursorShape = null;
        // Dispose cached cursor objects; they will be lazily re-created on next use.
        _cursorHand?.Dispose();
        _cursorIBeam?.Dispose();
        _cursorHand = null;
        _cursorIBeam = null;
        _selectionAdornerRects.Clear();
        if (_selectionAdorner is not null)
        {
            _selectionAdorner = null;
        }
        DetachSelectionHandleHandlers();
        _selectionStartHandle = null;
        _selectionEndHandle = null;
        _focusRing = null; // evicted from overlay above; lazily re-created on re-attach
        var snap = _snapshot;
        _snapshot = null;
        RetireSnapshot(snap);
    }

    private void RetireSnapshot(LayoutSnapshot? snapshot)
    {
        if (snapshot is null)
            return;

        _snapshotRetirements.Track(snapshot.Retire());
    }

    /// <summary>
    /// Releases renderer resources and detaches Win2D/XAML event handlers.
    /// </summary>
    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        _touchSelection.Dispose();
        StopSelectionHandleFrameClock();
        DetachWindowLifecycle();
        OnUnloaded();
        EffectiveViewportChanged -= OnEffectiveViewportChanged;
        DetachCanvasHandlers();
        DetachSelectionDragShieldHandlers();
        Content = null;
        _canvas = null;
        _overlay = null;
        _scroll = null;
        _root = null;
        MarkdownEngine? ownedEngine = _ownedEngine;
        _ownedEngine = null;
        ownedEngine?.Dispose();
        IDisposable? ownedHighlighter = _ownedCodeHighlighter;
        _ownedCodeHighlighter = null;
        _ownedCodeHighlighterService = null;
        if (ownedHighlighter is not null)
            RetireOwnedCodeHighlighter(ownedHighlighter, _codeBlockHighlightRetirement);
        try { DisposalCompleted?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine($"[MarkdownRendererControl] Disposal observer failed: {ex.Message}");
        }
    }

    // Release tooling observes actual control teardown without expanding the
    // public API or treating process disappearance as proof of disposal.
    internal event EventHandler? DisposalCompleted;

    // WinUI does not guarantee a child UserControl receives Unloaded when its
    // top-level window is destroyed. Subscribe to the non-cancelable window
    // destruction event so native Canvas resources are released while the
    // WinUI dispatcher and COM apartment are still valid.
    private void AttachWindowLifecycle()
    {
        if (XamlRoot?.ContentIslandEnvironment is not { } island)
            return;

        AppWindow appWindow;
        try
        {
            appWindow = AppWindow.GetFromWindowId(island.AppWindowId);
        }
        catch
        {
            // A host can transition between content islands while it is being
            // created or torn down. Its regular Unloaded path remains valid.
            return;
        }

        if (ReferenceEquals(_windowLifecycleAppWindow, appWindow))
            return;

        DetachWindowLifecycle();
        var subscription = new WindowLifecycleSubscription(this);
        _windowLifecycleAppWindow = appWindow;
        _windowLifecycleSubscription = subscription;
        appWindow.Destroying += subscription.OnDestroying;
    }

    private void DetachWindowLifecycle()
    {
        if (_windowLifecycleAppWindow is not { } appWindow)
            return;

        _windowLifecycleAppWindow = null;
        var subscription = _windowLifecycleSubscription;
        _windowLifecycleSubscription = null;
        if (subscription is null)
            return;

        try { appWindow.Destroying -= subscription.OnDestroying; }
        catch { /* The host may already be tearing down its WinRT event source. */ }
    }

    private sealed class WindowLifecycleSubscription
    {
        private readonly WeakReference<MarkdownRendererControl> _owner;

        internal WindowLifecycleSubscription(MarkdownRendererControl owner)
            => _owner = new WeakReference<MarkdownRendererControl>(owner);

        internal void OnDestroying(AppWindow sender, object args)
        {
            if (_owner.TryGetTarget(out var owner) &&
                ReferenceEquals(sender, owner._windowLifecycleAppWindow) &&
                !owner._isDisposed)
            {
                owner.Dispose();
            }
        }
    }

    private void DetachCanvasHandlers()
    {
        DetachCanvasRenderHandlers();
        DetachCanvasInputHandlers();
        GettingFocus -= OnGettingFocus;
        GotFocus -= OnGotFocus;
        LosingFocus -= OnLosingFocus;
        KeyDown -= OnKeyDown;
        DetachSelectionHandleHandlers();
    }

    private void DetachCanvasInputHandlers()
    {
        if (_canvas is not null)
        {
            _canvas.PointerPressed -= OnPointerPressed;
            _canvas.PointerWheelChanged -= OnPointerWheelChanged;
            _canvas.PointerExited -= OnPointerExited;
        _canvas.Tapped -= OnTapped;
        _canvas.DoubleTapped -= OnDoubleTapped;
        _canvas.Holding -= OnHolding;
            _canvas.RightTapped -= OnRightTapped;
            _canvas.RemoveHandler(UIElement.PointerMovedEvent, _canvasPointerMovedHandler);
            _canvas.RemoveHandler(UIElement.PointerReleasedEvent, _canvasPointerReleasedHandler);
            _canvas.RemoveHandler(UIElement.PointerCanceledEvent, _canvasPointerCanceledHandler);
            _canvas.RemoveHandler(UIElement.PointerCaptureLostEvent, _canvasPointerCaptureLostHandler);
        }

    }

    private void AttachCanvasRenderHandlers()
    {
        if (_canvas is null || _canvasRenderHandlersAttached)
            return;

        _canvas.RegionsInvalidated += OnRegionsInvalidated;
        _canvas.CreateResources += OnCanvasCreateResources;
        _canvasRenderHandlersAttached = true;
    }

    private void DetachCanvasRenderHandlers()
    {
        if (_canvas is null || !_canvasRenderHandlersAttached)
            return;

        _canvas.RegionsInvalidated -= OnRegionsInvalidated;
        _canvas.CreateResources -= OnCanvasCreateResources;
        _canvasRenderHandlersAttached = false;
    }

    private void DetachSelectionDragShieldHandlers()
    {
        if (_selectionDragShield is null)
            return;

        _selectionDragShield.PointerMoved -= OnPointerMoved;
        _selectionDragShield.PointerReleased -= OnPointerReleased;
        _selectionDragShield.PointerCanceled -= OnPointerCanceledOrCaptureLost;
        _selectionDragShield.PointerCaptureLost -= OnPointerCanceledOrCaptureLost;
        _selectionDragShield = null;
    }

    private void OnThemeChanged()
    {
        if (_isDisposed || _isUnloaded)
            return;

        // NotifyEnvironmentChanged fires Theme.Changed → OnThemeRevisionChanged → a restyle.
        // Do NOT call RequestRebuild() here again — that would start two simultaneous
        // builds and immediately cancel the first one on every theme change.
        if (Theme is { } t) t.NotifyEnvironmentChanged();
        else RequestRebuild(RebuildReason.Restyle); // no Theme object: must trigger rebuild directly
    }

    void IMarkdownEnvironmentListener.OnMarkdownEnvironmentChanged(
        in MarkdownEnvironmentSnapshot snapshot,
        MarkdownEnvironmentChange changes)
    {
        _environmentSnapshot = snapshot;
        if (_isDisposed || _isUnloaded)
            return;

        if ((changes & MarkdownEnvironmentChange.Relayout) != 0)
        {
            RequestRebuild(RebuildReason.Restyle);
            return;
        }

        // Layout boxes hold an immutable ThemeSnapshot. Until paint commands
        // carry semantic brush roles independently, a palette-only notification
        // must compile and commit a fresh snapshot; repainting the existing
        // boxes would retain stale accent or High Contrast colors.
        if (MarkdownEnvironmentSnapshot.IsBrushOnly(changes))
            RequestRebuild(RebuildReason.Restyle);
    }

    private void OnThemeDpChanged(DependencyPropertyChangedEventArgs e)
    {
        // Unsubscribe from the previous theme's Changed event so we don't leak
        // a reference to it (DependencyObjects don't auto-unsubscribe).
        if (e.OldValue is MarkdownTheme old)
        {
            old.Changed -= OnThemeRevisionChanged;
        }
        if (e.NewValue is MarkdownTheme @new)
        {
            @new.Changed += OnThemeRevisionChanged;
        }
        OnThemeChanged();
    }

    private void OnThemeRevisionChanged(object? sender, EventArgs e)
    {
        // A theme property, override, environment notification, or explicit
        // MarkdownTheme.Invalidate() raised Changed. Rebuild without notifying
        // the theme again, which would loop.
        // Guard against cross-thread calls: MarkdownTheme.Changed is a plain
        // .NET event and a consumer may call Invalidate() from a background thread.
        // Accessing DependencyObject members off the UI thread throws RPC_E_WRONG_THREAD.
        var dq = DispatcherQueue;
        if (dq is null) return;
        if (dq.HasThreadAccess) { if (!_isUnloaded) RequestRebuild(RebuildReason.Restyle); return; }
        dq.TryEnqueue(() => { if (!_isUnloaded) RequestRebuild(RebuildReason.Restyle); });
    }

    /// <summary>Kicks off (or re-kicks) the parse + layout pipeline.</summary>
    public void RequestRebuild()
        => RequestRebuild(RebuildReason.Full);

    /// <summary>
    /// Clears cached WinUI resource-key discovery and rebuilds the current
    /// presentation. Call this after replacing resource dictionary keys without
    /// changing the dictionary's count; ordinary value replacement, theme
    /// properties, and override mutations do not require it.
    /// </summary>
    public void InvalidateThemeResources()
    {
        if (_isDisposed)
            return;

        ThemeResolver.InvalidateResourceKeyCache();
        RequestRebuild(RebuildReason.Restyle);
    }

    private void RequestRebuild(RebuildReason reason)
    {
        if (_isDisposed)
            return;

        var dispatcher = DispatcherQueue;
        if (dispatcher is not null && !dispatcher.HasThreadAccess)
        {
            dispatcher.TryEnqueue(() => RequestRebuild(reason));
            return;
        }

        if (_isUnloaded)
            return;

        // Fence the currently active generation at request time, before the
        // queued rebuild callback can be delayed by UI work. This makes a
        // superseded pipeline unable to publish even while its parse worker is
        // still unwinding, and signals cancellation with request-time latency.
        ++_pipelineGeneration;
        var supersededCts = _pipelineCts;
        var supersededTask = _pipelineTask;
        long supersededGeneration = _activePipelineGeneration;
        _pipelineCts = null;
        _pipelineTask = null;
        _activePipelineGeneration = 0;

        long supersessionTimestamp = 0;
        var performance = MarkdownPerformanceEventSource.Log;
        if (supersededCts is not null &&
            supersededTask is { IsCompleted: false } &&
            supersededGeneration > 0 &&
            performance.IsMeasurementEnabled())
        {
            supersessionTimestamp = Stopwatch.GetTimestamp();
            performance.PipelineSuperseded(supersededGeneration);
        }

        RetireCancellationTokenSource(
            supersededCts,
            supersededTask,
            supersededGeneration,
            supersessionTimestamp);

        if (_pendingRebuildRequestTimestamp == 0 &&
            performance.IsMeasurementEnabled())
        {
            _pendingRebuildRequestTimestamp = Stopwatch.GetTimestamp();
        }

        _pendingRebuildReason = MergeRebuildReason(_pendingRebuildReason, reason);
        _hasPendingRebuild = true;

        if (!IsLoaded || _canvas is null || _overlay is null || _root is null)
            return;

        MarkdownRebuildDispatchPriority dispatchPriority =
            MarkdownRebuildDispatchPolicy.SelectPriority(
                _pendingRebuildReason == RebuildReason.Full);
        if (!_rebuildDispatchState.ShouldSchedule(dispatchPriority))
            return;

        if (dispatcher is null)
        {
            _rebuildDispatchState = default;
            ProcessQueuedRebuildCore();
            return;
        }

        long dispatchTicket = NextRebuildDispatchTicket();
        bool enqueued = dispatcher.TryEnqueue(
            ToDispatcherQueuePriority(dispatchPriority),
            () => ProcessQueuedRebuild(dispatchTicket));
        _rebuildDispatchState = _rebuildDispatchState.AfterEnqueueAttempt(
            dispatchTicket,
            dispatchPriority,
            enqueued);
    }

    private static RebuildReason MergeRebuildReason(RebuildReason current, RebuildReason incoming)
        => current == RebuildReason.Full || incoming == RebuildReason.Full
            ? RebuildReason.Full
            : RebuildReason.Restyle;

    private long NextRebuildDispatchTicket()
    {
        unchecked
        {
            ++_lastRebuildDispatchTicket;
            if (_lastRebuildDispatchTicket == 0)
                ++_lastRebuildDispatchTicket;
        }

        return _lastRebuildDispatchTicket;
    }

    private static Microsoft.UI.Dispatching.DispatcherQueuePriority ToDispatcherQueuePriority(
        MarkdownRebuildDispatchPriority priority) =>
        priority switch
        {
            MarkdownRebuildDispatchPriority.Low =>
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            MarkdownRebuildDispatchPriority.Normal =>
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal,
            _ => throw new ArgumentOutOfRangeException(nameof(priority)),
        };

    private void ProcessQueuedRebuild(long dispatchTicket)
    {
        if (!_rebuildDispatchState.IsCurrent(dispatchTicket))
            return;

        _rebuildDispatchState = default;
        ProcessQueuedRebuildCore();
    }

    private void ProcessQueuedRebuildCore()
    {
        if (_isUnloaded || !IsLoaded || !_hasPendingRebuild)
            return;

        // Image completion, theme changes, and width changes may request a
        // relayout while the user is actively selecting text. Releasing pointer
        // capture here terminates the gesture and visibly drops the selection.
        // Keep the latest merged rebuild request pending until pointer release.
        if (_pointerSession.IsActive || _horizontalOverflowCaptured ||
            _drainingPointerInput || (_deferredPointerInput?.Count ?? 0) > 0)
            return;

        var reason = _pendingRebuildReason;
        _pendingRebuildReason = RebuildReason.Restyle;
        _hasPendingRebuild = false;
        CancelLazyLayoutRealization();
        if (_imageRelayoutCts is { } imageRelayoutCancellation)
            _ = CancellationTokenSourceRetirement.RequestCancellation(imageRelayoutCancellation);

        string effectiveSource = _hasExplicitDocument
            ? Document?.Source ?? string.Empty
            : Markdown ?? string.Empty;
        bool sourceChanged = _snapshot is not { } currentSnapshot ||
            !string.Equals(currentSnapshot.SourceMap.SourceText, effectiveSource, StringComparison.Ordinal);
        ResetTransientInteractionState(clearSelection: sourceChanged);

        _pipelineCts = new CancellationTokenSource();
        var cts = _pipelineCts;
        long generation = _pipelineGeneration;
        _activePipelineGeneration = generation;
        var performance = MarkdownPerformanceEventSource.Log;
        long pipelineStartTimestamp = performance.IsMeasurementEnabled()
            ? Interlocked.Exchange(ref _pendingRebuildRequestTimestamp, 0)
            : 0;
        if (pipelineStartTimestamp == 0 && performance.IsMeasurementEnabled())
            pipelineStartTimestamp = Stopwatch.GetTimestamp();
        long sourceUtf16Bytes = (long)effectiveSource.Length * sizeof(char);
        if (pipelineStartTimestamp != 0)
            performance.PipelineStarted(generation, sourceUtf16Bytes);

        var task = RebuildAsync(
            cts.Token,
            reason,
            generation,
            pipelineStartTimestamp,
            sourceUtf16Bytes);
        _pipelineTask = task;
        _ = task.ContinueWith(t =>
        {
            if (t.IsFaulted)
                MarkdownDiagnostics.WriteLine($"[MarkdownRendererControl] Rebuild faulted: {t.Exception}");
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static Task RetireCancellationTokenSource(
        CancellationTokenSource? cts,
        Task? lifetime,
        long supersededGeneration = 0,
        long cancellationStartTimestamp = 0)
    {
        if (cts is null)
            return lifetime ?? Task.CompletedTask;

        var performance = MarkdownPerformanceEventSource.Log;
        Task retirement = CancellationTokenSourceRetirement.CancelAndDisposeAfter(
            cts,
            lifetime);

        if (cancellationStartTimestamp != 0 &&
            (lifetime is null || lifetime.IsCompleted))
        {
            performance.SupersededPipelineStopped(
                supersededGeneration,
                Stopwatch.GetTimestamp() - cancellationStartTimestamp);
        }
        else if (cancellationStartTimestamp != 0 && lifetime is not null)
        {
            _ = lifetime.ContinueWith(
                static (_, state) =>
                {
                    var observation = (PipelineCancellationObservation)state!;
                    MarkdownPerformanceEventSource.Log.SupersededPipelineStopped(
                        observation.Generation,
                        Stopwatch.GetTimestamp() - observation.CancellationStartTimestamp);
                },
                new PipelineCancellationObservation(
                    supersededGeneration,
                    cancellationStartTimestamp),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        return retirement;
    }

    private sealed record PipelineCancellationObservation(
        long Generation,
        long CancellationStartTimestamp);

    private void ResetTransientInteractionState(bool clearSelection)
    {
        CloseSelectionContextMenu();
        ResetSelectionHandleDrag();
        _touchSelection.Reset();
        _pointerSession = default;
        _selectionAnchor = null;
        _lastSelectionPointerViewportY = double.NaN;
        _clickMode = ClickMode.Single;
        _dragAnchorStart = default;
        _dragAnchorEnd = default;
        _consecutiveClickCount = 0;
        _lastPressTickMs = 0;
        _lastPressPoint = default;
        SetSelectionDragShieldActive(false);
        HideAbbreviationTooltip();

        if (_snapshot is { } snapshot)
        {
            foreach (var block in snapshot.Blocks)
                ClearHover(block);
        }

        _lastHoveredRun = null;
        _lastHoveredBox = null;
        SetCursorShape(null);

        try { _canvas?.ReleasePointerCaptures(); } catch { }

        if (!clearSelection)
            return;

        _selection.Clear();
        _selectionAdornerRects.Clear();
        InvalidateSelectionAdorner();
    }

    private async Task RebuildAsync(
        CancellationToken ct,
        RebuildReason reason,
        long generation,
        long pipelineStartTimestamp,
        long sourceUtf16Bytes)
    {
        try
        {
            await RebuildInternalAsync(
                ct,
                reason,
                generation,
                pipelineStartTimestamp,
                sourceUtf16Bytes).ConfigureAwait(true);
            if (!ct.IsCancellationRequested && generation == _pipelineGeneration)
            {
                RaiseRenderCompleted(generation);
            }
        }
        catch (OperationCanceledException) { /* expected – a new build was requested */ }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine($"[MarkdownRendererControl] Rebuild failed: {ex}");
            RaiseRenderFailed(generation, ex);
        }
    }

    private void RaiseRenderCompleted(long generation)
    {
        if (_isDisposed || _isUnloaded || generation != _pipelineGeneration)
            return;

        try
        {
            RenderCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine($"[MarkdownRendererControl] RenderCompleted subscriber failed: {ex.Message}");
        }

        PendingImagePrefetch? pending = Interlocked.Exchange(ref _pendingImagePrefetch, null);
        if (pending is not null)
        {
            _ = PrefetchImageSourcesObservedAsync(
                pending.Document,
                pending.SafeHtmlPolicy,
                pending.Prefetcher,
                pending.Context,
                pending.CancellationToken);
        }
    }

    private void RaiseRenderFailed(long generation, Exception exception)
    {
        if (_isDisposed || _isUnloaded || generation != _pipelineGeneration)
            return;

        try
        {
            RenderFailed?.Invoke(this, new MarkdownRenderFailedEventArgs(exception));
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine($"[MarkdownRendererControl] RenderFailed subscriber failed: {ex.Message}");
        }
    }

    private async Task<ParsedMarkdown?> GetParsedMarkdownAsync(
        string source,
        MarkdownExtensionRegistry registry,
        CancellationToken ct)
    {
        var normalizedSource = ForgivingDataUriFixer.Fix(source ?? string.Empty);
        int registryRevision = registry.Revision;
        lock (_parseCacheGate)
        {
            if (_parseCache is not null &&
                ReferenceEquals(_parseCacheRegistry, registry) &&
                _parseCacheRegistryRevision == registryRevision &&
                string.Equals(_parseCacheSource, normalizedSource, StringComparison.Ordinal))
            {
                return _parseCache;
            }
        }

        if (ct.IsCancellationRequested)
            return null;

        var pipeline = registry.BuildPipeline();
        var parser = new MarkdigParser(pipeline);
        var parsed = await parser.ParseAsync(normalizedSource, ct).ConfigureAwait(true);
        if (parsed is null || ct.IsCancellationRequested)
            return null;

        lock (_parseCacheGate)
        {
            _parseCache = parsed;
            _parseCacheSource = parsed.SourceText;
            _parseCacheRegistry = registry;
            _parseCacheRegistryRevision = registryRevision;
        }

        return parsed;
    }

    private async Task RebuildInternalAsync(
        CancellationToken ct,
        RebuildReason reason,
        long generation,
        long pipelineStartTimestamp,
        long sourceUtf16Bytes)
    {
        if (_canvas is null || _overlay is null || _root is null) return;
        var width = (float)Math.Max(50, ActualWidth);
        _lastWidth = width;

        MarkdownExtensionRegistry legacyRegistry = ExtensionRegistry ?? _defaultRegistry;
        MarkdownEngine? engineSnapshot = Engine;
        var reusableDocument = _hasExplicitDocument ? Document : null;
        var source = reusableDocument?.Source ?? Markdown ?? string.Empty;

        if (ct.IsCancellationRequested || generation != _pipelineGeneration)
            return;

        ParsedMarkdown? parsed;
        MarkdownRenderer.Document.MarkdownDocument? semanticDocument = reusableDocument;
        if (reusableDocument?.ParsedDocument is { } reusableAst)
        {
            parsed = new ParsedMarkdown(reusableDocument.Source, reusableAst);
        }
        else if (engineSnapshot is { } engine)
        {
            semanticDocument = await engine.ParseAsync(source, ct).ConfigureAwait(true);
            parsed = semanticDocument.ParsedDocument is { } engineAst
                ? new ParsedMarkdown(semanticDocument.Source, engineAst)
                : null;
        }
        else
        {
            parsed = await GetParsedMarkdownAsync(source, legacyRegistry, ct).ConfigureAwait(true);
            if (parsed is not null)
            {
                semanticDocument = await Task.Run(
                    () => MarkdownRenderer.Document.MarkdownDocument.FromParsed(
                        parsed.SourceText,
                        parsed.Document,
                        diagnostics: [],
                        ct),
                    CancellationToken.None).ConfigureAwait(true);
            }
        }

        if (parsed is null || semanticDocument is null || ct.IsCancellationRequested || generation != _pipelineGeneration)
            return;

        // Presentation registrations travel with reusable documents/engines.
        // Keep the legacy control registry only as the fallback parser/layout
        // source so an engine-produced document cannot silently lose its
        // custom renderers when it reaches the WinUI adapter.
        MarkdownExtensionRegistry baseRegistry =
            semanticDocument.PresentationConfiguration as MarkdownExtensionRegistry ??
            engineSnapshot?.PresentationConfiguration as MarkdownExtensionRegistry ??
            _defaultRegistry;
        MarkdownExtensionRegistry layoutRegistry =
            MarkdownExtensionRegistry.ComposePresentation(baseRegistry, ExtensionRegistry);

        var sourceMap = new MarkdownSourceMap(parsed.SourceText);
        var theme = Theme ?? _defaultTheme;
        MarkdownStyleSheet? styleSheetSnapshot = StyleSheet;
        FlowDirection flowDirectionSnapshot = FlowDirection;
        var embedFactorySnapshot = EmbedFactory;
        var hostedElementFactorySnapshot = HostedElementFactory;
        var imageResolverSnapshot = ImageResolver;
        IMarkdownPerformanceSessionInternal? performanceSessionSnapshot =
            PerformanceSession as IMarkdownPerformanceSessionInternal;
        if (performanceSessionSnapshot?.IsDisposed == true)
            performanceSessionSnapshot = null;
        var svgRendererSnapshot = SvgRenderer;
        Uri? imageBaseUriSnapshot = ImageBaseUri;
        string? imageDocumentPathSnapshot = ImageDocumentPath;
        MarkdownDocumentSource? imageDocumentSourceSnapshot = ImageDocumentSource;
        bool allowThirdPartyRemoteImagesSnapshot = AllowThirdPartyRemoteImages;
        var imageResolveContext = new MarkdownImageResolveContext(
            imageBaseUriSnapshot,
            imageDocumentPathSnapshot,
            allowThirdPartyRemoteImagesSnapshot,
            imageDocumentSourceSnapshot);
        if (performanceSessionSnapshot is not null && imageResolverSnapshot is not null)
        {
            if (_performanceDocumentScope is not { } currentScope ||
                !currentScope.Matches(performanceSessionSnapshot, imageResolverSnapshot, imageResolveContext) ||
                !string.Equals(_performanceDocumentSource, parsed.SourceText, StringComparison.Ordinal) ||
                _performanceRegistryRevision != layoutRegistry.Revision)
            {
                _performanceDocumentScope?.Dispose();
                _performanceDocumentScope = performanceSessionSnapshot.OpenDocument(
                    imageResolverSnapshot, imageResolveContext);
                _performanceDocumentSource = parsed.SourceText;
                _performanceRegistryRevision = layoutRegistry.Revision;
                _performancePrefetchStarted = false;
            }

            IMarkdownPerformanceDocumentScope scope = _performanceDocumentScope;
            imageResolverSnapshot = scope;
            if (!_performancePrefetchStarted && performanceSessionSnapshot.Options.PrefetchDocumentImages)
            {
                _performancePrefetchStarted = true;
                _ = PrefetchPerformanceImagesObservedAsync(
                    parsed.Document, layoutRegistry.SafeHtmlPolicy, scope);
            }
        }
        else
        {
            _performanceDocumentScope?.Dispose();
            _performanceDocumentScope = null;
            _performanceDocumentSource = null;
            _performanceRegistryRevision = -1;
            _performancePrefetchStarted = false;
            QueueImageSourcePrefetch(
                parsed.Document,
                parsed.SourceText,
                layoutRegistry,
                imageResolverSnapshot,
                imageResolveContext);
        }
        bool codeBlockCopyEnabledSnapshot = IsCodeBlockCopyEnabled;
        bool taskListEditingEnabledSnapshot = IsTaskListEditingEnabled;
        var commandProviderSnapshot = CommandProvider;
        MarkdownLocalizationSnapshot localizationSnapshot = CaptureRequestedLocalization();
        var stringProviderSnapshot = localizationSnapshot.StringProvider;
        string languageSnapshot = localizationSnapshot.Culture.Name;
        CodeBlockLineNumberMode lineNumberModeSnapshot = CodeBlockLineNumberMode;
        CodeBlockWrappingMode wrappingModeSnapshot = CodeBlockWrappingMode;
        var disclosureStatesSnapshot = new Dictionary<string, bool>(
            _disclosureStates,
            StringComparer.Ordinal);
        double textScaleFactor = _environmentSnapshot.TextScaleFactor > 0
            ? _environmentSnapshot.TextScaleFactor
            : 1.0;
        var themeSnapshot = new ThemeResolver(this, theme).CreateSnapshot(
            styleSheetSnapshot,
            textScaleFactor,
            semanticDocument.GetExtensionStyleRoleNames());
        // Use the shared CanvasDevice (always available, no visual-tree required).
        // CanvasVirtualControl only has a device after CreateResources fires, so
        // passing _canvas directly would crash if layout runs before first draw.
        var device = CanvasDevice.GetSharedDevice();
        // Capture the host's rasterization scale so raster-fallback image
        // paths (e.g. SvgSkiaRasterizer) render at device-pixel resolution.
        // XamlRoot is null until the control is loaded; default to 1.0.
        double rasterScale = XamlRoot?.RasterizationScale ?? 1.0;
        var ctx = new MarkdownLayoutContext(
            device,
            themeSnapshot,
            sourceMap,
            layoutRegistry,
            flowDirectionSnapshot,
            DispatcherQueue,
            languageSnapshot)
        {
            RasterizationScale = rasterScale,
            // Propagate the rebuild lifetime through layout and host image
            // construction. Image resolution has a separate loaded-lifetime
            // token so benign width/theme relayouts do not cancel committed
            // image boxes before they can publish their result.
            CancellationToken = ct,
            ImageCancellationToken = _imageLifetimeCts?.Token ?? CancellationToken.None,
            IsCodeBlockCopyEnabled = codeBlockCopyEnabledSnapshot,
            IsTaskListEditingEnabled = taskListEditingEnabledSnapshot,
            CommandProvider = commandProviderSnapshot,
            TaskCommandAvailabilityChanged = OnTaskCommandAvailabilityChanged,
            StringProvider = stringProviderSnapshot,
            CodeBlockLineNumberMode = lineNumberModeSnapshot,
            CodeBlockWrappingMode = wrappingModeSnapshot,
            ImageResolver = imageResolverSnapshot,
            PerformanceSession = performanceSessionSnapshot,
            PerformanceDocumentOwner = this,
            SvgRenderer = svgRendererSnapshot,
            ImageBaseUri = imageBaseUriSnapshot,
            ImageDocumentPath = imageDocumentPathSnapshot,
            ImageDocumentSource = imageDocumentSourceSnapshot,
            AllowThirdPartyRemoteImages = allowThirdPartyRemoteImagesSnapshot,
            ImageUnavailable = RaiseImageUnavailable,
            DisclosureStates = disclosureStatesSnapshot,
        };
        SharedLayoutMetricsKey? sharedLayoutMetricsKey = engineSnapshot is null
            ? null
            : new SharedLayoutMetricsKey(
                parsed.SourceText,
                semanticDocument,
                widthInSixtyFourthDips: (int)Math.Clamp(
                    Math.Round(width * 64d),
                    1d,
                    int.MaxValue),
                typographyFingerprint: themeSnapshot.LayoutFingerprint,
                flowDirection: (int)flowDirectionSnapshot,
                codeWrappingMode: (int)wrappingModeSnapshot,
                lineNumberMode: (int)lineNumberModeSnapshot,
                codeCopyEnabled: codeBlockCopyEnabledSnapshot,
                taskEditingEnabled: taskListEditingEnabledSnapshot,
                rasterScaleInThousandths: (int)Math.Clamp(
                    Math.Round(rasterScale * 1_000d),
                    1d,
                    int.MaxValue),
                language: languageSnapshot,
                registryRevision: layoutRegistry.Revision,
                disclosureFingerprint: ComputeDisclosureFingerprint(disclosureStatesSnapshot),
                imageContext: new SharedLayoutImageContext(
                    imageBaseUriSnapshot?.OriginalString,
                    imageDocumentPathSnapshot,
                    allowThirdPartyRemoteImagesSnapshot,
                    imageDocumentSourceSnapshot),
                styleSheetSnapshot,
                layoutRegistry,
                embedFactorySnapshot,
                hostedElementFactorySnapshot,
                imageResolverSnapshot,
                svgRendererSnapshot,
                commandProviderSnapshot,
                stringProviderSnapshot);
        var builder = new LayoutBuilder(
            ctx,
            embedFactorySnapshot,
            enableDeclarativeHostedElements: hostedElementFactorySnapshot is not null,
            semanticDocument,
            new HashSet<HostedElementFallbackKey>(_hostedElementFallbacks),
            engineSnapshot,
            sharedLayoutMetricsKey);

        if (ct.IsCancellationRequested || generation != _pipelineGeneration)
            return;

        TryGetViewport(out double viewportTop, out double viewportHeight, out _);
        // Custom block embed measurement remains on the background build path.
        // Subsequent lazy-band extension is also dispatched to a background
        // worker, so IMarkdownEmbedFactory.MeasureHeight and text reflow never
        // run from viewport or paint callbacks.
        bool useLazyLayout = MarkdownLazyLayoutPolicy.ShouldUse(
            parsed.Document.Count,
            parsed.SourceText.Length,
            hasCustomBlockEmbedMeasurement: embedFactorySnapshot is not null);
        double initialLazyOverscan = performanceSessionSnapshot is null
            ? LazyLayoutOverscanPx
            : Math.Clamp(
                viewportHeight * performanceSessionSnapshot.Options.LookAheadViewports,
                0,
                6000);
        var snapshot = await Task.Run(
            () => BuildSnapshotOrNullOnCancellation(
                builder,
                parsed.Document,
                width,
                viewportTop,
                viewportHeight,
                useLazyLayout,
                initialLazyOverscan,
                ct),
            CancellationToken.None).ConfigureAwait(true);
        if (snapshot is null || ct.IsCancellationRequested || generation != _pipelineGeneration)
        {
            snapshot?.Dispose();
            return;
        }

        // From this point the snapshot holds GPU-side CanvasTextLayout objects.
        // If we are cancelled before committing, dispose it to avoid a native-memory leak.
        // `committed` tracks whether the snapshot was written to _snapshot; the catch
        // block must only dispose it if not yet committed (post-commit _snapshot owns it).
        bool committed = false;
        try
        {
        if (ct.IsCancellationRequested || generation != _pipelineGeneration)
            return;

        // Scroll anchoring: capture the current read position before the canvas
        // height changes.  If the user has scrolled down, we identify the first
        // visible block (its top edge closest to viewport top) and how far it is
        // from the viewport top.  After committing the new layout we restore the
        // same offset so content above the fold shifting (e.g. an image loading)
        // doesn't jump the reader's position.
        (int? BlockIndex, double OffsetFromTop, double OldOffset, double OldHeight)? scrollAnchor = null;
        if (_scroll is { VerticalOffset: > 0 } scrollSnap && _snapshot is { } prevSnap)
        {
            double vTop = scrollSnap.VerticalOffset;
            if (prevSnap.TryCaptureScrollAnchor(vTop, out var capturedAnchor))
            {
                scrollAnchor = (
                    capturedAnchor.BlockIndex,
                    capturedAnchor.OffsetFromTop,
                    vTop,
                    capturedAnchor.Height);
            }
            else
            {
                // Lazy measurement still owns the old snapshot. Preserve a
                // proportional fallback from the already-committed XAML extent
                // rather than blocking this UI-thread replacement for its lock.
                scrollAnchor = (null, 0, vTop, _root?.Height ?? 0);
            }
        }

        if (ct.IsCancellationRequested || generation != _pipelineGeneration)
            return;

        // Atomically swap snapshots, then retire the old one so its
        // CanvasTextLayout / placeholder handles are released without making
        // this UI-thread commit wait for an in-flight lazy measure.
        var old = _snapshot;
        bool sameDocument = old is not null &&
            string.Equals(old.SourceMap.SourceText, snapshot.SourceMap.SourceText, StringComparison.Ordinal);
        bool semanticInputChanged = old is null ||
            !sameDocument ||
            !ReferenceEquals(Document, semanticDocument) ||
            !ReferenceEquals(_committedLocalization?.StringProvider, stringProviderSnapshot) ||
            !string.Equals(
                _committedLocalization?.Culture.Name,
                languageSnapshot,
                StringComparison.OrdinalIgnoreCase);
        bool preserveSelection = !_selection.Range.Normalized().IsEmpty && sameDocument;
        LinkRun? focusedLink = null;
        if (sameDocument && FocusState != FocusState.Unfocused && TryGetFocusedLink(out _, out var currentFocusedLink))
            focusedLink = currentFocusedLink;
        // Scrolling or paint can queue another band realization while this
        // replacement pipeline is building. Fence that old-snapshot work at
        // the commit boundary before publishing and retiring its owner.
        CancelLazyLayoutRealization();
        _snapshot = snapshot;
        // Publish presentation state with the new snapshot before any
        // dependency-property notification can re-enter UIA or menu queries.
        // These immutable objects therefore always describe the same commit.
        _themeSnapshot = themeSnapshot;
        // The tile clear color is presentation state too. Publish it only with
        // the snapshot whose boxes use the matching immutable ThemeSnapshot;
        // otherwise an invalidation during background layout can clear using
        // the next theme and repaint using the previous one.
        _canvasBackground = themeSnapshot.SurfaceColor;
        _committedLocalization = localizationSnapshot;
        _snapshotGeneration = generation;
        _snapshotPipelineStartTimestamp = pipelineStartTimestamp;
        _snapshotSourceUtf16Bytes = sourceUtf16Bytes;
        committed = true;
        _appliedLazyLayoutRevision = snapshot.LayoutRevision;
        if (!ReferenceEquals(Document, semanticDocument))
        {
            _isUpdatingDocumentProperty = true;
            try { SetValue(DocumentProperty, semanticDocument); }
            finally { _isUpdatingDocumentProperty = false; }
        }
        RetireSnapshot(old);
        // Clear stale hover references so OnPointerExited/OnPointerMoved after the
        // rebuild don't use Bounds from the now-disposed old snapshot for invalidation.
        _lastHoveredRun = null;
        _lastHoveredBox = null;
        ApplySnapshotSize(snapshot);

        // Restore scroll anchor: find the anchor block's new Y in the new layout
        // and adjust the scroll offset so the user's read position is unchanged.
        if (scrollAnchor is { } anchor && _scroll is { } scrollRestore)
        {
            double? newY = null;
            if (anchor.BlockIndex is { } anchorBlock)
            {
                foreach (var b in snapshot.Blocks)
                {
                    if (b.BlockIndex == anchorBlock)
                    {
                        newY = b.Bounds.Top - anchor.OffsetFromTop;
                        break;
                    }
                }

                newY ??= FindNearestScrollAnchor(snapshot, anchorBlock, anchor.OffsetFromTop);
            }

            if (newY is null)
            {
                double ratio = anchor.OldHeight > 0
                    ? anchor.OldOffset / anchor.OldHeight
                    : 0;
                newY = ratio > 0
                    ? ratio * snapshot.Size.Height
                    : anchor.OldOffset;
            }

            if (newY is { } targetOffset)
            {
                double maxOffset = Math.Max(0, snapshot.Size.Height - scrollRestore.ViewportHeight);
                scrollRestore.ChangeView(null, Math.Clamp(targetOffset, 0, maxOffset), null, disableAnimation: true);
            }
        }

        // UI thread: collect embed plans (don't realise yet), hook image
        // LoadCompleted, then realise only embeds that fall in the current
        // viewport. Hooking _scroll.ViewChanged drives subsequent realisation
        // as the user scrolls.
        DerealizeAllEmbeds();
        SetSelectionDragShieldActive(false);
        ReleaseSelectionAdornerSurface();
        if (_selectionAdorner is not null)
            _selectionAdorner.Visibility = Visibility.Collapsed;
        _overlay.Children.Clear();
        _embedRects.Clear();
        _blockEmbedRects.Clear();
        _codeBlockActionRects.Clear();
        _embedPlans.Clear();
        _codeBlockActionPlans.Clear();
        UnsubscribeAllImages();
        _imagePlans.Clear();
        _imagePlanIndex = null;
        // Identities change across rebuild even when the count happens to
        // match — reset so the first post-rebuild realisation always fires.
        _lastFiredRealizedCount = -1;
        _selectionAdornerRects.Clear();
        // Rebuild detaches selection chrome with the other overlay children.
        // Surviving selection reattaches what it needs below; inactive chrome
        // stays detached until the next interaction.
        if (preserveSelection)
            UpdateSelectionOverlay();
        else
            _selection.Clear();
        _focusableItems = snapshot.CollectFocusableItems();
        _focusedItemIndex = focusedLink is not null && TryGetFocusableIndexForLink(focusedLink, out var restoredFocusIndex)
            ? restoredFocusIndex
            : -1;
        _focusResumeItemIndex = -1;
        _pendingLazyFocus = null;
        _focusRing = null;              // evicted from overlay; will be lazily re-created on demand
        RebuildRealizationPlans(snapshot, preserveRealized: false);
        if (_scroll is not null)
        {
            _scroll.ViewChanged -= OnScrollViewChanged;
            _scroll.ViewChanged += OnScrollViewChanged;
        }
        RealizeVisibleEmbeds();
        _ = RestartCodeBlockHighlighting();
        ScheduleVisibleCodeBlockHighlighting();
        UpdateSelectionAdornerViewport();
        UpdateFocusRing();

        InvalidateCanvas();
        if (semanticInputChanged)
        {
            (FrameworkElementAutomationPeer.FromElement(this) as MarkdownAutomationPeer)?
                .NotifyDocumentChanged();
        }
        } // end of snapshot try-block
        catch
        {
            throw;
        }
        finally
        {
            // Every cancellation/generation retry after construction must release
            // the unpublished Win2D layout. Once committed, _snapshot owns it.
            if (!committed)
                snapshot.Dispose();
        }
    }

    private static LayoutSnapshot? BuildSnapshotOrNullOnCancellation(
        LayoutBuilder builder,
        Markdig.Syntax.MarkdownDocument document,
        float width,
        double viewportTop,
        double viewportHeight,
        bool useLazyLayout,
        double overscan,
        CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return null;

        try
        {
            return useLazyLayout
                ? builder.BuildLazy(document, width, viewportTop, viewportHeight, overscan, ct)
                : builder.Build(document, width, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private void OnScrollViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        var performance = MarkdownPerformanceEventSource.Log;
        bool measure = performance.IsMeasurementEnabled();
        long started = measure ? Stopwatch.GetTimestamp() : 0;
        long allocatedBefore = measure ? GC.GetAllocatedBytesForCurrentThread() : 0;
        long afterLazyLayout = allocatedBefore;
        long afterAdorner = allocatedBefore;
        long afterRealization = allocatedBefore;
        try
        {
            if (ShakeLogger.IsEnabled && _scroll is not null)
            {
                ShakeLogger.Log(
                    "scroll-view-changed",
                    $"offset={_scroll.VerticalOffset:F2} viewport={_scroll.ViewportHeight:F2} " +
                    $"extent={_scroll.ExtentHeight:F2} intermediate={e.IsIntermediate}");
            }
            QueueLazyLayoutForViewport();
            if (measure)
                afterLazyLayout = GC.GetAllocatedBytesForCurrentThread();
            UpdateSelectionAdornerViewport();
            // Scrolling CanvasVirtualControl already requests newly exposed
            // tiles. Avoid a second full invalidation when there is no
            // selection/link/focus adorner to repaint; the WinRT invalidate
            // call alone allocates several KiB per scroll frame.
            if (HasInteractiveTextAdornerContent())
                InvalidateSelectionAdorner();
            if (measure)
                afterAdorner = GC.GetAllocatedBytesForCurrentThread();
            // Run a final realisation pass after intermediate-view bursts settle
            // so we don't thrash during fling/inertia. ViewChanged with
            // IsIntermediate=false fires at the end of inertia; we also realise on
            // intermediate ticks to keep the visual current.
            RealizeVisibleEmbeds();
            if (measure)
                afterRealization = GC.GetAllocatedBytesForCurrentThread();
            ScheduleVisibleCodeBlockHighlighting();
        }
        finally
        {
            if (measure)
            {
                long allocated = Math.Max(
                    0,
                    GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
                performance.ScrollWork(
                    MarkdownPerformanceEventSource.GetLogicalFrameId(),
                    Stopwatch.GetTimestamp() - started,
                    allocated);
                performance.ScrollAllocationBreakdown(
                    MarkdownPerformanceEventSource.GetLogicalFrameId(),
                    Math.Max(0, afterLazyLayout - allocatedBefore),
                    Math.Max(0, afterAdorner - afterLazyLayout),
                    Math.Max(0, afterRealization - afterAdorner),
                    Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - afterRealization));
                GetCodeBlockActionRealizationCounts(out int realizedActions, out int offscreenActions);
                performance.CodeActionRealization(
                    MarkdownPerformanceEventSource.GetLogicalFrameId(),
                    realizedActions,
                    offscreenActions);
            }
        }
    }

    private Task RestartCodeBlockHighlighting()
        => RetireCodeBlockHighlighting(restart: true);

    private Task RetireCodeBlockHighlighting(bool restart)
    {
        var old = _codeBlockHighlightCts;
        Task workLifetime = OwnedResourceRetirement.CombineLifetimes(
            _codeBlockHighlightRetirement,
            DrainCodeBlockHighlightTasks());
        _codeBlockHighlightCts = restart ? new CancellationTokenSource() : null;
        _codeBlockHighlightGeneration++;
        _codeBlockHighlightInFlight.Clear();
        Task retirement = RetireCancellationTokenSource(old, workLifetime);
        _codeBlockHighlightRetirement = retirement;
        return retirement;
    }

    private static void RetireOwnedCodeHighlighter(IDisposable owner, Task lifetime)
    {
        _ = OwnedResourceRetirement.DisposeAfter(
            owner,
            lifetime,
            static exception => MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] Code highlighter retirement failed: {exception.Message}"));
    }

    private void ScheduleVisibleCodeBlockHighlighting()
    {
        if (!IsCodeBlockSyntaxHighlightingEnabled ||
            CodeHighlighter is not { } highlighter ||
            _snapshot is not { } snapshot ||
            _themeSnapshot is not { } theme ||
            theme.IsHighContrast)
        {
            return;
        }

        var cts = _codeBlockHighlightCts;
        if (cts is null || cts.IsCancellationRequested)
            return;

        var variant = ResolveCodeBlockThemeVariant(theme);
        int generation = _codeBlockHighlightGeneration;
        int providerIdentity = RuntimeHelpers.GetHashCode(highlighter);
        int providerRevision = highlighter.Revision;
        IMarkdownPerformanceSessionInternal? sceneSession =
            PerformanceSession as IMarkdownPerformanceSessionInternal;
        if (sceneSession?.IsDisposed == true)
            return;
        bool appliedCached = false;
        TryGetViewport(out double viewportTop, out double viewportHeight, out _);
        LazyLayoutBand highlightBand = PerformanceSession is { } performanceSession
            ? LazyLayoutBand.FromDirectionalViewport(
                viewportTop,
                viewportHeight,
                performanceSession.Options.LookAheadViewports,
                _lazyScrollVelocityPixelsPerSecond)
            : LazyLayoutBand.FromViewport(
                viewportTop,
                viewportHeight,
                CodeBlockHighlightOverscanPx);
        IEnumerable<Layout.Boxes.CodeBlockBox> candidates;
        if (PerformanceSession is null)
        {
            candidates = EnumerateCodeBlocks(snapshot);
        }
        else
        {
            // The background layout worker can own this snapshot. Scrolling
            // must not wait for its native text measurement merely to queue
            // syntax highlighting; completion reschedules the visible band.
            if (!snapshot.TryGetMeasuredTopLevelBlocksInBand(
                    highlightBand.Top,
                    highlightBand.Bottom,
                    out IReadOnlyList<BlockBox> bandBlocks))
                return;
            candidates = EnumerateCodeBlocks(bandBlocks);
        }
        foreach (var block in candidates)
        {
            if (!IsCodeBlockInHighlightBand(block, highlightBand.Top, highlightBand.Bottom))
                continue;

            if (block.CodeText.Length > 200_000 || block.LineCount > 5_000)
                continue;

            var key = CreateHighlightCacheKey(block, variant, providerIdentity, providerRevision);
            if (block.HasAppliedSyntaxHighlighting(key))
                continue;
            if (_codeBlockHighlightCache.TryGetValue(key, out var cached))
            {
                appliedCached |= block.ApplySyntaxHighlighting(key, cached.Spans);
                continue;
            }

            if (!_codeBlockHighlightInFlight.Add(key))
                continue;

            TrackCodeBlockHighlightTask(
                HighlightCodeBlockAsync(snapshot, block, key, variant, highlighter, providerIdentity, providerRevision, generation, sceneSession, cts.Token));
        }

        if (appliedCached)
            InvalidateCanvas();
    }

    private void TrackCodeBlockHighlightTask(Task task)
    {
        lock (_codeBlockHighlightTasksGate)
        {
            _codeBlockHighlightTasks.Add(task);
        }

        _ = task.ContinueWith(
            static (completed, state) =>
            {
                var owner = (MarkdownRendererControl)state!;
                lock (owner._codeBlockHighlightTasksGate)
                {
                    owner._codeBlockHighlightTasks.Remove(completed);
                }
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private Task DrainCodeBlockHighlightTasks()
    {
        Task[] tasks;
        lock (_codeBlockHighlightTasksGate)
        {
            if (_codeBlockHighlightTasks.Count == 0)
                return Task.CompletedTask;

            tasks = new Task[_codeBlockHighlightTasks.Count];
            _codeBlockHighlightTasks.CopyTo(tasks);
            _codeBlockHighlightTasks.Clear();
        }

        return Task.WhenAll(tasks);
    }

    private async Task HighlightCodeBlockAsync(
        LayoutSnapshot snapshot,
        Layout.Boxes.CodeBlockBox block,
        CodeBlockHighlightCacheKey key,
        CodeBlockThemeVariant variant,
        ICodeHighlighter highlighter,
        int providerIdentity,
        int providerRevision,
        int generation,
        IMarkdownPerformanceSessionInternal? sceneSession,
        CancellationToken token)
    {
        if (token.IsCancellationRequested)
        {
            DispatcherQueue.TryEnqueue(() => RemoveCodeBlockHighlightInFlight(key, generation));
            return;
        }

        try
        {
            await _codeBlockHighlightSemaphore.WaitAsync(token).ConfigureAwait(false);
            try
            {
                using IMarkdownScenePreparationLease? sceneLease = sceneSession is null
                    ? null
                    : await sceneSession.EnterScenePreparationAsync(this, token)
                        .ConfigureAwait(false);
                CancellationToken workToken = sceneLease?.CancellationToken ?? token;
                if (workToken.IsCancellationRequested)
                    return;

                var request = new CodeBlockHighlightRequest(block.CodeLanguage, block.CodeText, variant, workToken);
                var result = await highlighter.HighlightAsync(request, workToken).ConfigureAwait(false)
                    ?? CodeBlockHighlightResult.Empty;
                if (workToken.IsCancellationRequested)
                    return;

                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_isUnloaded ||
                        workToken.IsCancellationRequested ||
                        sceneSession?.IsDisposed == true ||
                        !ReferenceEquals(_snapshot, snapshot) ||
                        !CodeBlockHighlightPublicationFence.IsCurrent(
                            CodeHighlighter,
                            highlighter,
                            providerRevision,
                            _codeBlockHighlightGeneration,
                            generation))
                    {
                        return;
                    }
                    _codeBlockHighlightCache.Set(key, result);
                    RemoveCodeBlockHighlightInFlight(key, generation);
                    if (_codeBlockHighlightCache.TryGetValue(key, out _))
                    {
                        // The visible-band scheduler applies the retained
                        // result to matching blocks without a document scan.
                        ScheduleVisibleCodeBlockHighlighting();
                    }
                    else if (ApplyUncachedCodeBlockHighlightResult(
                        snapshot, key, variant, providerIdentity, providerRevision, result))
                    {
                        // Oversized results cannot enter the bounded cache.
                        // Publish to the current band directly, without
                        // immediately requeuing the same expensive request.
                        InvalidateCanvas();
                    }
                });
            }
            finally
            {
                _codeBlockHighlightSemaphore.Release();
            }
        }
        catch (OperationCanceledException)
        {
            DispatcherQueue.TryEnqueue(() => RemoveCodeBlockHighlightInFlight(key, generation));
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine($"[MarkdownRendererControl] Code block highlighting failed: {ex.Message}");
            DispatcherQueue.TryEnqueue(() => RemoveCodeBlockHighlightInFlight(key, generation));
        }
    }

    private void RemoveCodeBlockHighlightInFlight(CodeBlockHighlightCacheKey key, int generation)
    {
        if (generation == _codeBlockHighlightGeneration)
            _codeBlockHighlightInFlight.Remove(key);
    }

    private bool ApplyUncachedCodeBlockHighlightResult(
        LayoutSnapshot snapshot,
        CodeBlockHighlightCacheKey key,
        CodeBlockThemeVariant variant,
        int providerIdentity,
        int providerRevision,
        CodeBlockHighlightResult result)
    {
        IEnumerable<Layout.Boxes.CodeBlockBox> candidates;
        LazyLayoutBand band = default;
        if (PerformanceSession is null)
        {
            candidates = EnumerateCodeBlocks(snapshot);
        }
        else
        {
            TryGetViewport(out double viewportTop, out double viewportHeight, out _);
            band = LazyLayoutBand.FromDirectionalViewport(
                viewportTop,
                viewportHeight,
                PerformanceSession.Options.LookAheadViewports,
                _lazyScrollVelocityPixelsPerSecond);
            if (!snapshot.TryGetMeasuredTopLevelBlocksInBand(
                    band.Top, band.Bottom, out IReadOnlyList<BlockBox> bandBlocks))
                return false;
            candidates = EnumerateCodeBlocks(bandBlocks);
        }

        bool changed = false;
        foreach (var candidate in candidates)
        {
            if (PerformanceSession is not null &&
                !IsCodeBlockInHighlightBand(candidate, band.Top, band.Bottom))
                continue;
            if (CreateHighlightCacheKey(candidate, variant, providerIdentity, providerRevision).Equals(key))
                changed |= candidate.ApplySyntaxHighlighting(key, result.Spans);
        }
        return changed;
    }

    private static bool IsCodeBlockInHighlightBand(Layout.Boxes.CodeBlockBox block, double top, double bottom)
        => block.Bounds.Bottom >= top && block.Bounds.Top <= bottom;

    private static IEnumerable<Layout.Boxes.CodeBlockBox> EnumerateCodeBlocks(LayoutSnapshot snapshot)
        => EnumerateCodeBlocks(snapshot.GetMeasuredTopLevelBlocks());

    private static IEnumerable<Layout.Boxes.CodeBlockBox> EnumerateCodeBlocks(
        IReadOnlyList<BlockBox> blocks)
    {
        foreach (var block in blocks)
        {
            foreach (var codeBlock in EnumerateCodeBlocks(block))
                yield return codeBlock;
        }
    }

    private static IEnumerable<Layout.Boxes.CodeBlockBox> EnumerateCodeBlocks(BlockBox block)
    {
        switch (block)
        {
            case Layout.Boxes.CodeBlockBox codeBlock:
                yield return codeBlock;
                break;
            case Layout.Boxes.ListItemBox listItem:
                foreach (var item in EnumerateCodeBlocks(listItem.Marker))
                    yield return item;
                foreach (var item in EnumerateCodeBlocks(listItem.Content))
                    yield return item;
                break;
            case Layout.Boxes.StackBox stack:
                foreach (var child in stack.Children)
                {
                    foreach (var item in EnumerateCodeBlocks(child))
                        yield return item;
                }
                break;
        }
    }

    private CodeBlockHighlightCacheKey CreateHighlightCacheKey(
        Layout.Boxes.CodeBlockBox block,
        CodeBlockThemeVariant variant,
        int providerIdentity,
        int providerRevision)
        => new(block.CodeLanguage, block.Metadata.CodeTextHash, block.CodeText.Length, variant, providerIdentity, providerRevision);

    private void ClearCodeBlockHighlightCache()
        => _codeBlockHighlightCache.Clear();

    private static CodeBlockThemeVariant ResolveCodeBlockThemeVariant(Theming.ThemeSnapshot theme)
    {
        if (theme.IsHighContrast)
            return CodeBlockThemeVariant.HighContrast;

        var bg = theme.SurfaceColor;
        double luminance = (0.2126 * bg.R + 0.7152 * bg.G + 0.0722 * bg.B) / 255.0;
        return luminance < 0.5 ? CodeBlockThemeVariant.Dark : CodeBlockThemeVariant.Light;
    }

    private static ulong ComputeDisclosureFingerprint(
        IReadOnlyDictionary<string, bool> disclosureStates)
    {
        if (disclosureStates.Count == 0)
            return 0;

        const ulong prime = 1099511628211UL;
        var keys = new List<string>(disclosureStates.Keys);
        keys.Sort(StringComparer.Ordinal);
        ulong hash = 14695981039346656037UL;
        foreach (string key in keys)
        {
            hash ^= Fnv1A64(key);
            hash *= prime;
            hash ^= disclosureStates[key] ? 1UL : 0UL;
            hash *= prime;
        }

        return hash;
    }

    private static ulong Fnv1A64(string value)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offset;
        foreach (var ch in value)
        {
            hash ^= ch;
            hash *= prime;
        }

        return hash;
    }

    private void ApplySnapshotSize(LayoutSnapshot snapshot)
    {
        if (_canvas is null || _root is null || _overlay is null) return;
        _canvas.Width = Math.Max(1, snapshot.Size.Width);
        _canvas.Height = Math.Max(1, snapshot.Size.Height);
        _root.Width = _canvas.Width;
        _root.Height = _canvas.Height;
        _overlay.Width = _canvas.Width;
        _overlay.Height = _canvas.Height;
        SetSelectionDragShieldActive(_selectionAnchor is not null);
    }

    private static (int BlockIndex, double OffsetFromTop)? CaptureScrollAnchor(LayoutSnapshot snapshot, double verticalOffset)
    {
        if (verticalOffset <= 0)
            return null;

        foreach (var b in snapshot.Blocks)
        {
            if (b.Bounds.Bottom < verticalOffset) continue;
            return (b.BlockIndex, b.Bounds.Top - verticalOffset);
        }

        return null;
    }

    private void RestoreScrollAnchor(LayoutSnapshot snapshot, (int BlockIndex, double OffsetFromTop)? anchor)
    {
        if (anchor is not { } value || _scroll is null)
            return;

        foreach (var b in snapshot.Blocks)
        {
            if (b.BlockIndex != value.BlockIndex)
                continue;

            double target = Math.Max(0, b.Bounds.Top - value.OffsetFromTop);
            if (Math.Abs(target - _scroll.VerticalOffset) >= 0.5)
                _scroll.ChangeView(null, target, null, disableAnimation: true);
            return;
        }
    }

    private void QueueLazyLayoutForViewport()
    {
        var snapshot = _snapshot;
        if (snapshot is null || !snapshot.IsLazyLayoutEnabled)
            return;

        TryGetViewport(out double viewportTop, out double viewportHeight, out _);
        LazyLayoutBand band;
        if (PerformanceSession is { } performanceSession)
        {
            long now = Stopwatch.GetTimestamp();
            if (_lastLazyViewportTimestamp > 0)
            {
                double elapsedSeconds = (now - _lastLazyViewportTimestamp) /
                    (double)Stopwatch.Frequency;
                if (elapsedSeconds is > 0.002 and < 1)
                {
                    _lazyScrollVelocityPixelsPerSecond =
                        (viewportTop - _lastLazyViewportTop) / elapsedSeconds;
                }
            }
            _lastLazyViewportTop = viewportTop;
            _lastLazyViewportTimestamp = now;
            band = LazyLayoutBand.FromDirectionalViewport(
                viewportTop,
                viewportHeight,
                performanceSession.Options.LookAheadViewports,
                _lazyScrollVelocityPixelsPerSecond);
        }
        else
        {
            band = LazyLayoutBand.FromViewport(
                viewportTop, viewportHeight, LazyLayoutOverscanPx);
        }

        if (snapshot.IsBandMeasured(band))
            return;

        QueueLazyLayoutForBand(snapshot, band, preserveScrollAnchor: _scroll is not null);
    }

    private void QueueLazyLayoutForPaintRegion(Rect region)
    {
        var snapshot = _snapshot;
        if (snapshot is null || !snapshot.IsLazyLayoutEnabled)
            return;

        var band = PerformanceSession is { } performanceSession
            ? LazyLayoutBand.FromDirectionalViewport(
                region.Top,
                region.Height,
                performanceSession.Options.LookAheadViewports,
                _lazyScrollVelocityPixelsPerSecond)
            : LazyLayoutBand.FromViewport(region.Top, region.Height, LazyLayoutOverscanPx);
        if (snapshot.IsBandMeasured(band))
            return;

        QueueLazyLayoutForBand(snapshot, band, preserveScrollAnchor: _scroll is not null);
    }

    private void QueueLazyLayoutForBand(
        LayoutSnapshot snapshot,
        LazyLayoutBand band,
        bool preserveScrollAnchor)
    {
        if (_isDisposed || _isUnloaded || !ReferenceEquals(snapshot, _snapshot))
            return;

        if (_lazyLayoutCts is { IsCancellationRequested: false } &&
            ReferenceEquals(snapshot, _activeLazyLayoutSnapshot) &&
            band.Top >= _activeLazyLayoutBand.Top &&
            band.Bottom <= _activeLazyLayoutBand.Bottom &&
            (!preserveScrollAnchor ||
             (_scroll is not null &&
              _activeLazyLayoutAnchorOffset is { } activeOffset &&
              Math.Abs(_scroll.VerticalOffset - activeOffset) < 0.5)))
        {
            return;
        }

        if (_lazyLayoutCts is { } activeCancellation)
            _ = CancellationTokenSourceRetirement.RequestCancellation(activeCancellation);

        if (_hasPendingLazyLayoutBand && ReferenceEquals(snapshot, _pendingLazyLayoutSnapshot))
        {
            _pendingLazyLayoutBand = new LazyLayoutBand(
                Math.Min(_pendingLazyLayoutBand.Top, band.Top),
                Math.Max(_pendingLazyLayoutBand.Bottom, band.Bottom));
            _pendingLazyLayoutPreserveScrollAnchor |= preserveScrollAnchor;
        }
        else
        {
            _pendingLazyLayoutSnapshot = snapshot;
            _pendingLazyLayoutBand = band;
            _pendingLazyLayoutPreserveScrollAnchor = preserveScrollAnchor;
            _hasPendingLazyLayoutBand = true;
        }

        if (_lazyLayoutDispatchQueued)
            return;

        _lazyLayoutDispatchQueued = true;
        var dispatcher = DispatcherQueue;
        if (dispatcher is null || !dispatcher.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                ProcessQueuedLazyLayoutRealization))
        {
            _lazyLayoutDispatchQueued = false;
        }
    }

    private void ProcessQueuedLazyLayoutRealization()
    {
        _lazyLayoutDispatchQueued = false;
        if (_isDisposed || _isUnloaded || !_hasPendingLazyLayoutBand)
            return;

        LayoutSnapshot? snapshot = _pendingLazyLayoutSnapshot;
        LazyLayoutBand band = _pendingLazyLayoutBand;
        bool preserveScrollAnchor = _pendingLazyLayoutPreserveScrollAnchor;
        _pendingLazyLayoutSnapshot = null;
        _hasPendingLazyLayoutBand = false;
        _pendingLazyLayoutPreserveScrollAnchor = false;

        if (snapshot is null || !ReferenceEquals(snapshot, _snapshot))
            return;

        var oldCts = _lazyLayoutCts;
        var oldTask = _lazyLayoutTask;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(
            _pipelineCts?.Token ?? CancellationToken.None);
        long realizationGeneration = ++_lazyLayoutGeneration;
        long pipelineGeneration = _pipelineGeneration;
        double? anchorOffset = preserveScrollAnchor && _scroll is not null
            ? _scroll.VerticalOffset
            : null;
        var dispatcher = DispatcherQueue;

        _lazyLayoutCts = cts;
        _activeLazyLayoutSnapshot = snapshot;
        _activeLazyLayoutBand = band;
        _activeLazyLayoutAnchorOffset = anchorOffset;
        _lazyLayoutTask = RunLazyLayoutRealizationAsync(
            snapshot,
            band,
            anchorOffset,
            pipelineGeneration,
            realizationGeneration,
            cts,
            dispatcher);
        RetireCancellationTokenSource(oldCts, oldTask);
    }

    private async Task RunLazyLayoutRealizationAsync(
        LayoutSnapshot snapshot,
        LazyLayoutBand band,
        double? anchorOffset,
        long pipelineGeneration,
        long realizationGeneration,
        CancellationTokenSource cts,
        Microsoft.UI.Dispatching.DispatcherQueue? dispatcher)
    {
        LazyLayoutWorkResult result;
        Exception? failure = null;
        try
        {
            result = await Task.Run(
                () =>
                {
                    var anchor = anchorOffset is { } offset
                        ? snapshot.CaptureScrollAnchor(offset)
                        : null;
                    LazyLayoutCommit commit = snapshot.EnsureMeasuredBand(band, cts.Token);
                    return new LazyLayoutWorkResult(commit, anchor);
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            result = default;
            failure = ex;
        }

        if (dispatcher is null || !dispatcher.TryEnqueue(() => CompleteLazyLayoutRealization(
                snapshot,
                result,
                failure,
                pipelineGeneration,
                realizationGeneration,
                cts)))
        {
            // Mark this request supersedable when its completion cannot reach
            // the UI thread. A later viewport request can then retire it instead
            // of treating the abandoned band as permanently active.
            _ = CancellationTokenSourceRetirement.RequestCancellation(cts);
        }
    }

    private void CompleteLazyLayoutRealization(
        LayoutSnapshot snapshot,
        LazyLayoutWorkResult result,
        Exception? failure,
        long pipelineGeneration,
        long realizationGeneration,
        CancellationTokenSource cts)
    {
        if (_isDisposed ||
            _isUnloaded ||
            cts.IsCancellationRequested ||
            realizationGeneration != _lazyLayoutGeneration ||
            pipelineGeneration != _pipelineGeneration ||
            !ReferenceEquals(snapshot, _snapshot))
        {
            return;
        }

        var completedCts = _lazyLayoutCts;
        var completedTask = _lazyLayoutTask;
        _lazyLayoutCts = null;
        _lazyLayoutTask = null;
        _activeLazyLayoutSnapshot = null;
        _activeLazyLayoutAnchorOffset = null;
        RetireCancellationTokenSource(completedCts, completedTask);

        if (failure is not null)
        {
            if (GraphicsDeviceErrors.IsDeviceLost(failure))
            {
                HandleCanvasDeviceLost(failure);
            }
            else
            {
                MarkdownDiagnostics.WriteLine($"[MarkdownRendererControl] lazy layout realization failed: {failure}");
                RequestRebuild();
            }
            return;
        }

        if (result.Commit.LayoutRevision <= _appliedLazyLayoutRevision)
        {
            // A focus request can arrive while an already-measured band is
            // publishing its completion. Re-evaluate it even when that worker
            // produced no newer layout revision.
            _ = ResumePendingLazyFocus(snapshot);
            return;
        }

        _appliedLazyLayoutRevision = result.Commit.LayoutRevision;
        ApplyLazyLayoutCommit(snapshot, result.ScrollAnchor);
    }

    private void CancelLazyLayoutRealization()
    {
        _lazyLayoutGeneration++;
        _lazyLayoutDispatchQueued = false;
        _pendingLazyLayoutSnapshot = null;
        _activeLazyLayoutSnapshot = null;
        _activeLazyLayoutAnchorOffset = null;
        _hasPendingLazyLayoutBand = false;
        _pendingLazyLayoutPreserveScrollAnchor = false;
        _pendingLazyFocus = null;

        var oldCts = _lazyLayoutCts;
        var oldTask = _lazyLayoutTask;
        _lazyLayoutCts = null;
        _lazyLayoutTask = null;
        RetireCancellationTokenSource(oldCts, oldTask);
    }

    private void ApplyLazyLayoutCommit(
        LayoutSnapshot snapshot,
        (int BlockIndex, double OffsetFromTop)? scrollAnchor)
    {
        ApplySnapshotSize(snapshot);
        RestoreScrollAnchor(snapshot, scrollAnchor);
        RebuildRealizationPlans(snapshot, preserveRealized: true);
        Layout.FocusableItem? focusedIdentity = GetCurrentFocusableIdentity();
        _focusableItems = snapshot.CollectFocusableItems();
        if (focusedIdentity is { } identity)
            _focusedItemIndex = FindFocusableItemIndex(_focusableItems, identity);
        RealizeVisibleEmbeds();
        ScheduleVisibleCodeBlockHighlighting();
        if (!ResumePendingLazyFocus(snapshot))
            UpdateFocusRing();
        RefreshInteractiveTextAdornerAfterLayoutChange();
        InvalidateAutomationLayout();
        InvalidateCanvas();
    }

    private void DerealizeAllEmbeds()
    {
        _derealizingAllEmbeds = true;
        // Previously pooled buttons may still be collapsed children of the
        // current overlay. The caller clears that overlay after this pass, so
        // discard those attached-state entries before pooling current plans.
        _codeBlockCopyButtonPool.Clear();
        try
        {
            foreach (var plan in _embedPlans)
                plan.Derealize(this);
            foreach (var plan in _codeBlockActionPlans)
            {
                if (plan.Realized is not null) plan.Derealize(this);
            }
        }
        finally
        {
            _derealizingAllEmbeds = false;
        }
    }

    private void RebuildRealizationPlans(LayoutSnapshot snapshot, bool preserveRealized)
    {
        var oldPlans = preserveRealized ? _embedPlans.ToArray() : Array.Empty<EmbedPlan>();
        var oldActionPlans = preserveRealized ? _codeBlockActionPlans.ToArray() : Array.Empty<CodeBlockActionPlan>();

        UnsubscribeAllImages();
        _imagePlans.Clear();
        _imagePlanIndex = null;
        _embedRects.Clear();
        _blockEmbedRects.Clear();
        _codeBlockActionRects.Clear();
        _embedPlans.Clear();
        _codeBlockActionPlans.Clear();

        foreach (var b in snapshot.GetMeasuredTopLevelBlocks())
            CollectEmbedPlans(b);

        // Image plans are immutable until the next layout publication. Keep
        // their document order in the list, but query the measured viewport
        // through a vertical index instead of walking every image on a scroll.
        if (_imagePlans.Count > 0)
        {
            _imagePlanIndex = new ViewportBandIndex(_imagePlans.Count);
            for (int index = 0; index < _imagePlans.Count; index++)
            {
                Rect bounds = _imagePlans[index].Bounds;
                _imagePlanIndex.SetEntry(index, index, bounds.Top, bounds.Bottom);
            }
            _imagePlanIndex.Commit();
        }

        if (oldPlans.Length > 0)
        {
            // Only plans owned by the same immutable box can be the same
            // logical embed. Index realized plans once: a long document can
            // otherwise spend O(n²) UI-thread work after every lazy-layout or
            // image-size publication.
            var oldByOwner = new Dictionary<object, List<EmbedPlan>>(
                ReferenceEqualityComparer.Instance);
            foreach (EmbedPlan oldPlan in oldPlans)
            {
                if (oldPlan.Realized is null)
                    continue;
                if (!oldByOwner.TryGetValue(oldPlan.LogicalOwner, out var candidates))
                    oldByOwner.Add(oldPlan.LogicalOwner, candidates = []);
                candidates.Add(oldPlan);
            }

            foreach (var newPlan in _embedPlans)
            {
                if (!oldByOwner.TryGetValue(newPlan.LogicalOwner, out var candidates))
                    continue;
                for (int index = 0; index < candidates.Count; index++)
                {
                    EmbedPlan oldPlan = candidates[index];
                    if (!newPlan.IsSameLogicalEmbed(oldPlan))
                        continue;

                    if (newPlan is DeclarativeHostedElementPlan newDeclarative &&
                        oldPlan is DeclarativeHostedElementPlan oldDeclarative)
                    {
                        newDeclarative.AdoptRealizedFrom(oldDeclarative);
                    }
                    else
                    {
                        newPlan.Realized = oldPlan.Realized;
                        oldPlan.Realized = null;
                        AttachRealizedElement(newPlan);
                    }
                    newPlan.UpdatePlacement();
                    candidates.RemoveAt(index);
                    break;
                }
            }

            foreach (var oldPlan in oldPlans)
            {
                oldPlan.Derealize(this);
            }
        }

        if (oldActionPlans.Length > 0)
        {
            var oldByBox = new Dictionary<Layout.Boxes.CodeBlockBox, List<CodeBlockActionPlan>>(
                ReferenceEqualityComparer.Instance);
            foreach (CodeBlockActionPlan oldPlan in oldActionPlans)
            {
                if (oldPlan.Realized is null)
                    continue;
                if (!oldByBox.TryGetValue(oldPlan.Box, out var candidates))
                    oldByBox.Add(oldPlan.Box, candidates = []);
                candidates.Add(oldPlan);
            }

            foreach (var newPlan in _codeBlockActionPlans)
            {
                if (!oldByBox.TryGetValue(newPlan.Box, out var candidates))
                    continue;
                for (int index = 0; index < candidates.Count; index++)
                {
                    CodeBlockActionPlan oldPlan = candidates[index];
                    if (!newPlan.IsSameLogicalAction(oldPlan))
                        continue;

                    newPlan.AdoptRealizedFrom(oldPlan, this);
                    candidates.RemoveAt(index);
                    break;
                }
            }

            foreach (var oldPlan in oldActionPlans)
            {
                if (oldPlan.Realized is not null)
                    oldPlan.Derealize(this);
            }
        }

        _lastFiredRealizedCount = -1;
    }

    private static void AttachRealizedElement(EmbedPlan plan)
    {
        switch (plan)
        {
            case BlockEmbedPlan block:
                block.Box.RealizedElement = block.Realized;
                break;
            case InlineEmbedPlan inline:
                inline.Run.RealizedElement = inline.Realized;
                break;
            case DeclarativeHostedElementPlan declarative:
                declarative.Box.RealizedElement = declarative.Realized;
                break;
        }
    }

    /// <summary>
    /// Realise embeds whose rect intersects the realisation band (viewport +
    /// overscan) and derealise embeds that have left the wider derealisation
    /// band. Called from snapshot commit, scroll, and resize.
    /// </summary>
    internal void RealizeVisibleEmbeds()
    {
        TryGetViewport(out double top, out double viewportHeight, out _);
        double bottom = top + viewportHeight;

        // Lazy image loading: trigger EnsureLoading for images within the
        // viewport + LazyImageOverscanPx band.  Images already started (cached
        // or previously triggered) are silently skipped by EnsureLoading().
        LazyLayoutBand imageBand = PerformanceSession is { } performanceSession
            ? LazyLayoutBand.FromDirectionalViewport(
                top,
                viewportHeight,
                performanceSession.Options.LookAheadViewports,
                _lazyScrollVelocityPixelsPerSecond)
            : LazyLayoutBand.FromViewport(top, viewportHeight, LazyImageOverscanPx);
        double imgLoadTop = imageBand.Top;
        double imgLoadBottom = imageBand.Bottom;
        ViewportRange imageRange = _imagePlanIndex?.Find(imgLoadTop, imgLoadBottom) ?? default;
        for (int index = imageRange.Start; index < imageRange.End; index++)
        {
            var img = _imagePlans[_imagePlanIndex!.GetBlockOrdinal(index)];
            if (img.Bounds.Bottom >= imgLoadTop && img.Bounds.Top <= imgLoadBottom)
                img.EnsureLoading();
        }

        // Drop old realisation-side caches; they'll be repopulated from realised plans.
        _embedRects.Clear();
        _blockEmbedRects.Clear();
        _codeBlockActionRects.Clear();

        foreach (var plan in _codeBlockActionPlans)
        {
            double actionTop = plan.Rect.Top;
            double actionBottom = plan.Rect.Bottom;
            // Copy buttons are XAML controls, so retaining them in an overscan
            // band creates off-screen automation peers and warm-scroll churn.
            // Realize only actions that intersect the effective viewport; the
            // detached bounded pool makes boundary crossings inexpensive.
            bool inRealize = actionBottom >= top && actionTop <= bottom;
            if (inRealize)
            {
                plan.Realize(this);
            }
            else
            {
                plan.Derealize(this);
            }

            if (plan.Realized is not null)
            {
                double left = Math.Round(plan.Rect.X);
                double t = Math.Round(plan.Rect.Y);
                double w = Math.Round(plan.Rect.X + plan.Rect.Width) - left;
                double h = Math.Round(plan.Rect.Y + plan.Rect.Height) - t;
                _codeBlockActionRects.Add((plan.Box, new Rect(left, t, w, h), plan.Kind));
            }
        }

        if (_embedPlans.Count == 0)
        {
            // No embeds: still emit a transition-to-zero event so subscribers
            // that mirror the count (e.g. UI-automation) don't go stale after
            // a rebuild that dropped all embeds.
            if (_lastFiredRealizedCount != 0)
            {
                _lastFiredRealizedCount = 0;
                try { EmbedsRealizationChanged?.Invoke(this, EventArgs.Empty); }
                catch { /* swallow */ }
            }
            return;
        }

        foreach (var plan in _embedPlans)
        {
            double pTop = plan.Rect.Top;
            double pBottom = plan.Rect.Bottom;
            bool inRealize = EmbedVisibility.IsInRealizeBand(pTop, pBottom, top, bottom, EmbedVirtualizationOverscanPx);
            bool inDerealize = EmbedVisibility.IsInDerealizeBand(pTop, pBottom, top, bottom, EmbedVirtualizationDerealizeOverscanPx);
            if (inRealize)
            {
                plan.Realize(this);
            }
            else if (!inDerealize)
            {
                plan.Derealize(this);
            }
            // else: outside realise band but inside derealise band → keep current state (hysteresis).

            // Rebuild hit-rect caches from realised plans.
            if (plan.Realized is not null)
            {
                if (plan is BlockEmbedPlan bp)
                {
                    double left = Math.Round(bp.Box.Bounds.X + bp.Box.Margin.Left);
                    double t = Math.Round(bp.Box.Bounds.Y + bp.Box.Margin.Top);
                    double w = Math.Round(bp.Box.Bounds.Width  - bp.Box.Margin.Left - bp.Box.Margin.Right);
                    double h = Math.Round(bp.Box.Bounds.Height - bp.Box.Margin.Top  - bp.Box.Margin.Bottom);
                    _blockEmbedRects.Add((bp.Box, new Rect(left, t, w, h)));
                }
                else if (plan is InlineEmbedPlan ip)
                {
                    double iLeft = Math.Round(ip.Rect.X);
                    double iTop  = Math.Round(ip.Rect.Y);
                    double iW    = Math.Round(ip.Rect.X + ip.Rect.Width)  - iLeft;
                    double iH    = Math.Round(ip.Rect.Y + ip.Rect.Height) - iTop;
                    _embedRects.Add((ip.Icb, ip.Run, new Rect(iLeft, iTop, iW, iH)));
                }
                else if (plan is DeclarativeHostedElementPlan declarative)
                {
                    _blockEmbedRects.Add((declarative.Box, declarative.Rect));
                }
            }
        }

        // Surface the realised count to subscribers (e.g. sample app exposing
        // it via a hidden TextBlock for UI-automation tests) — but only when
        // the count actually changed. Firing on every scroll tick causes a
        // property-change event storm for any UIA listener bound to a Name
        // property mirror in the subscriber's UI.
        int realised = 0;
        foreach (var p in _embedPlans) if (p.Realized is not null) realised++;
        if (realised != _lastFiredRealizedCount)
        {
            _lastFiredRealizedCount = realised;
            InvalidateAutomationLayout();
            try { EmbedsRealizationChanged?.Invoke(this, EventArgs.Empty); }
            catch { /* subscriber threw — swallow to keep scroll pipeline alive. */ }
        }
    }

    private void GetCodeBlockActionRealizationCounts(out int realizedCount, out int offscreenCount)
    {
        TryGetViewport(out double top, out double viewportHeight, out _);
        double bottom = top + viewportHeight;
        realizedCount = 0;
        offscreenCount = 0;
        foreach (var plan in _codeBlockActionPlans)
        {
            if (plan.Realized is null)
                continue;
            realizedCount++;
            if (plan.Rect.Bottom < top || plan.Rect.Top > bottom)
                offscreenCount++;
        }
    }

    private MarkdownHostedElementRequest CreateHostedElementRequest(
        Layout.Boxes.DeclarativeHostedElementBox box,
        Rect layoutBounds)
    {
        TryGetViewport(out double viewportTop, out double viewportHeight, out double viewportWidth);
        return new MarkdownHostedElementRequest(
            box.FactoryKey,
            box.SourceRange,
            layoutBounds,
            new Rect(0, viewportTop, Math.Max(0, viewportWidth), Math.Max(0, viewportHeight)),
            box.Attributes,
            box.AccessibilityRole,
            box.AccessibilityName,
            box.AccessibilityDescription,
            box.SemanticText,
            box.AutomationId);
    }

    private bool CanAttachHostedElement(
        DeclarativeHostedElementPlan plan,
        long pipelineGeneration)
    {
        if (_isUnloaded ||
            pipelineGeneration != _pipelineGeneration ||
            _overlay is null ||
            !_embedPlans.Contains(plan))
            return false;

        TryGetViewport(out double top, out double viewportHeight, out _);
        double bottom = top + viewportHeight;
        return EmbedVisibility.IsInRealizeBand(
            plan.Rect.Top,
            plan.Rect.Bottom,
            top,
            bottom,
            EmbedVirtualizationOverscanPx);
    }

    private bool CanActivateHostedElementFallback(
        DeclarativeHostedElementPlan plan,
        long pipelineGeneration)
        => !_isUnloaded &&
           pipelineGeneration == _pipelineGeneration &&
           _embedPlans.Contains(plan);

    private void ActivateHostedElementFallback(DeclarativeHostedElementPlan plan)
    {
        if (!_hostedElementFallbacks.Add(plan.Box.FallbackKey))
            return;

        MarkdownDiagnostics.WriteLine(
            $"[MarkdownRendererControl] using native markdown fallback for hosted element '{plan.Box.FactoryKey}'.");
        RequestRebuild(RebuildReason.Restyle);
    }

    private static void ApplyHostedElementAutomationProperties(
        Layout.Boxes.DeclarativeHostedElementBox box,
        FrameworkElement element)
    {
        // The semantic wrapper owns the logical identity across virtualization.
        // Give its realized native child a stable, distinct id when the host
        // did not provide one (or copied the logical request id verbatim).
        string nativeAutomationId = AutomationProperties.GetAutomationId(element);
        if (string.IsNullOrWhiteSpace(nativeAutomationId) ||
            string.Equals(nativeAutomationId, box.AutomationId, StringComparison.Ordinal))
        {
            AutomationProperties.SetAutomationId(element, $"{box.AutomationId}-Native");
        }

        // Host-owned names and descriptions remain authoritative when supplied.
        if (!string.IsNullOrWhiteSpace(box.AccessibilityName) &&
            string.IsNullOrWhiteSpace(AutomationProperties.GetName(element)))
        {
            AutomationProperties.SetName(element, box.AccessibilityName);
        }

        if (!string.IsNullOrWhiteSpace(box.AccessibilityDescription) &&
            string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(element)))
        {
            AutomationProperties.SetHelpText(element, box.AccessibilityDescription);
        }
    }

    private void RestoreFocusedHostedElementIfNeeded(DeclarativeHostedElementPlan plan)
    {
        if (FocusState == FocusState.Unfocused ||
            plan.Realized is null ||
            _focusableItems is not { } items ||
            _focusedItemIndex < 0 ||
            _focusedItemIndex >= items.Count)
        {
            return;
        }

        var focusedItem = items[_focusedItemIndex];
        if (focusedItem.IsDeclarativeHostedElement &&
            focusedItem.BlockIndex == plan.Box.BlockIndex)
        {
            _ = TryFocusHostedElement(focusedItem, reverse: false);
        }
    }

    private void CollectEmbedPlans(Layout.BlockBox box)
    {
        switch (box)
        {
            case Layout.Boxes.EmbedBox eb:
            {
                double left = eb.Bounds.X + eb.Margin.Left;
                double top  = eb.Bounds.Y + eb.Margin.Top;
                double w = eb.Bounds.Width  - eb.Margin.Left - eb.Margin.Right;
                double h = eb.Bounds.Height - eb.Margin.Top  - eb.Margin.Bottom;
                _embedPlans.Add(new BlockEmbedPlan { Box = eb, Rect = new Rect(left, top, w, h) });
                break;
            }
            case Layout.Boxes.DeclarativeHostedElementBox hosted when HostedElementFactory is { } factory:
            {
                double left = hosted.Bounds.X + hosted.Margin.Left;
                double top = hosted.Bounds.Y + hosted.Margin.Top;
                double width = hosted.Bounds.Width - hosted.Margin.Left - hosted.Margin.Right;
                double height = hosted.Bounds.Height - hosted.Margin.Top - hosted.Margin.Bottom;
                _embedPlans.Add(new DeclarativeHostedElementPlan
                {
                    Box = hosted,
                    Factory = factory,
                    Rect = new Rect(left, top, Math.Max(0, width), Math.Max(0, height)),
                });
                break;
            }
            case Layout.Boxes.ImageBox ib:
            {
                AddImagePlan(ib);
                break;
            }
            case Layout.Boxes.InlineContainerBox icb:
            {
                // Register completion before asking DirectWrite for rectangles. During the
                // first commit an inline image can be measurable but not yet have a stable
                // character region; paint will start it and completion must still reflow.
                foreach (var run in icb.Runs)
                {
                    if (run is InlineImageRun imageRun)
                    {
                        // Image registration must not depend on DirectWrite
                        // returning a character rectangle for the object
                        // replacement slot. In particular, a linked image on
                        // an otherwise empty line can temporarily have no
                        // region during the first layout pass. Keeping it out
                        // of _imagePlans in that state leaves it permanently
                        // "Loading" because no later viewport pass can start
                        // it. A missing first-pass rectangle has the default
                        // origin, which deliberately starts the bounded load;
                        // EnumerateInlineImageRects synchronizes the real
                        // bounds as soon as DirectWrite publishes them.
                        AddImagePlan(imageRun.Image);
                    }
                }
                foreach (var (run, rect) in icb.EnumerateEmbedRects())
                {
                    _embedPlans.Add(new InlineEmbedPlan { Icb = icb, Run = run, Rect = rect });
                }
                // Force enumeration so each ImageBox receives its document
                // bounds. Images were already added above, independently of
                // whether this geometry query succeeds.
                foreach (var _ in icb.EnumerateInlineImageRects()) { }
                break;
            }
            case Layout.Boxes.CodeBlockBox codeBlock:
            {
                if (codeBlock.IsCopyButtonEnabled && codeBlock.CopyButtonBounds.Width > 0 && codeBlock.CopyButtonBounds.Height > 0)
                    _codeBlockActionPlans.Add(new CodeBlockActionPlan { Box = codeBlock, Rect = codeBlock.CopyButtonBounds, Kind = CodeBlockHostedElementKind.Copy });
                break;
            }
            case Layout.Boxes.ListItemBox lib:
                CollectEmbedPlans(lib.Marker);
                CollectEmbedPlans(lib.Content);
                break;
            case Layout.Boxes.TableBox tb:
                foreach (var cell in tb.GetCellBoxes()) CollectEmbedPlans(cell);
                break;
            case Layout.Boxes.StackBox sb:
                foreach (var c in sb.Children) CollectEmbedPlans(c);
                break;
        }
    }

    private void AddImagePlan(Layout.Boxes.ImageBox image)
    {
        if (!RegisterImage(image))
            return;

        _imagePlans.Add(image);
    }

    private void QueueImageSourcePrefetch(
        Markdig.Syntax.MarkdownDocument document,
        string markdownSource,
        MarkdownExtensionRegistry registry,
        IMarkdownImageResolver? resolver,
        MarkdownImageResolveContext context)
    {
        if (resolver is not IMarkdownImagePrefetcher prefetcher ||
            _imageLifetimeCts is not { IsCancellationRequested: false } imageLifetime)
        {
            return;
        }

        if (ReferenceEquals(_activeImagePrefetcher, prefetcher) &&
            string.Equals(_prefetchedMarkdownSource, markdownSource, StringComparison.Ordinal) &&
            Equals(_activeImagePrefetchContext, context) &&
            _prefetchedImageRegistryRevision == registry.Revision)
        {
            return;
        }

        _activeImagePrefetcher = prefetcher;
        _prefetchedMarkdownSource = markdownSource;
        _activeImagePrefetchContext = context;
        _prefetchedImageRegistryRevision = registry.Revision;
        _pendingImagePrefetch = new PendingImagePrefetch(
            document,
            registry.SafeHtmlPolicy,
            prefetcher,
            context,
            imageLifetime.Token);
    }

    private static async Task PrefetchImageSourcesObservedAsync(
        Markdig.Syntax.MarkdownDocument document,
        SafeHtmlRenderPolicy? safeHtmlPolicy,
        IMarkdownImagePrefetcher prefetcher,
        MarkdownImageResolveContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<string> sources = await Task.Run(
                    () => MarkdownImagePrefetchSourceCollector.Collect(
                        document,
                        safeHtmlPolicy,
                        cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            if (sources.Count > 0)
            {
                await prefetcher.PrefetchAsync(sources, context, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            MarkdownDiagnostics.WriteLine($"[MarkdownRenderer] image prefetch failed: {exception.Message}");
        }
    }

    private static async Task PrefetchPerformanceImagesObservedAsync(
        Markdig.Syntax.MarkdownDocument document,
        SafeHtmlRenderPolicy? safeHtmlPolicy,
        IMarkdownPerformanceDocumentScope scope)
    {
        try
        {
            CancellationToken cancellationToken = scope.CancellationToken;
            IReadOnlyList<string> sources = await Task.Run(
                () => MarkdownImagePrefetchSourceCollector.Collect(
                    document, safeHtmlPolicy, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            await scope.PrefetchAsync(sources, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception exception)
        {
            MarkdownDiagnostics.WriteLine($"[MarkdownRenderer] performance prefetch failed: {exception.Message}");
        }
    }

    private bool RegisterImage(Layout.Boxes.ImageBox image)
    {
        if (!_subscribedImages.Add(image))
            return false;

        image.LoadCompleted += OnImageLoadCompleted;
        return true;
    }

    private void UnsubscribeAllImages()
    {
        foreach (var image in _subscribedImages)
        {
            image.LoadCompleted -= OnImageLoadCompleted;
        }

        _subscribedImages.Clear();
    }

    private void OnImageLoadCompleted(object? sender, Layout.Boxes.LoadCompletedEventArgs e)
    {
        // CanvasBitmap.LoadAsync continues on a thread-pool thread. Always
        // marshal to the UI thread because relayout and invalidation touch
        // UI-owned state. Drop the
        // event silently if we have no dispatcher (control already unloaded).
        var dq = DispatcherQueue;
        if (dq is null) return;
        bool layoutInvalidated = e?.LayoutInvalidated ?? true;
        dq.TryEnqueue(() =>
        {
            // Guard against the TOCTOU window where this lambda was already
            // dispatched before OnUnloaded ran its unsubscription.
            if (_isUnloaded) return;
            Layout.Boxes.ImageBox? completedImage = sender as Layout.Boxes.ImageBox;
            if (completedImage is not null)
            {
                if (!_subscribedImages.Contains(completedImage))
                    return;

                (FrameworkElementAutomationPeer.FromElement(this) as MarkdownAutomationPeer)?
                    .NotifyImageStatusChanged(completedImage);
            }
            if (layoutInvalidated)
            {
                if (completedImage is not null)
                {
                    QueueImageRelayout(completedImage.BlockIndex);
                }
                else
                {
                    // The normal event contract always supplies its ImageBox.
                    // Fall back to a rebuild if a custom source violates it;
                    // guessing a dirty block could leave stale geometry behind.
                    RequestRebuild();
                }
            }
            else
            {
                // Paint-only (e.g. SVG re-parsed against current device). Don't
                // rebuild: doing so would dispose the freshly-parsed _svg and
                // start the reparse cycle over again.
                InvalidateCanvas();
                InvalidateSelectionAdorner();
            }
        });
    }

    private void QueueImageRelayout(int blockIndex)
    {
        if (_isDisposed || _isUnloaded)
            return;

        _pendingImageRelayoutBlockIndices.Add(blockIndex);
        if (_imageRelayoutQueued || _imageRelayoutActive)
            return;

        _imageRelayoutQueued = true;
        Microsoft.UI.Dispatching.DispatcherQueue? dispatcher = DispatcherQueue;
        if (dispatcher is null || !dispatcher.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                ProcessQueuedImageRelayout))
        {
            _imageRelayoutQueued = false;
        }
    }

    private void ProcessQueuedImageRelayout()
    {
        if (!_imageRelayoutQueued)
            return;

        _imageRelayoutQueued = false;
        LayoutSnapshot? snapshot = _snapshot;
        if (_isDisposed || _isUnloaded || snapshot is null ||
            _pendingImageRelayoutBlockIndices.Count == 0)
            return;

        if (PerformanceSession is not null)
        {
            int[] changed = [.. _pendingImageRelayoutBlockIndices];
            _pendingImageRelayoutBlockIndices.Clear();
            (int BlockIndex, double OffsetFromTop)? anchor = null;
            if (_scroll is { VerticalOffset: > 0 } scroll &&
                snapshot.TryCaptureScrollAnchor(scroll.VerticalOffset, out var captured) &&
                captured.BlockIndex is { } blockIndex)
            {
                anchor = (blockIndex, captured.OffsetFromTop);
            }

            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _pipelineCts?.Token ?? CancellationToken.None);
            _imageRelayoutActive = true;
            _imageRelayoutCts = cancellation;
            _imageRelayoutTask = RunImageRelayoutAsync(
                snapshot,
                changed,
                (float)Math.Max(50, ActualWidth),
                anchor,
                _pipelineGeneration,
                cancellation,
                DispatcherQueue);
            return;
        }

        try
        {
            int[] dirtyBlockIndices = [.. _pendingImageRelayoutBlockIndices];
            _pendingImageRelayoutBlockIndices.Clear();
            (int BlockIndex, double OffsetFromTop)? anchor = _scroll is null
                ? null
                : CaptureScrollAnchor(snapshot, _scroll.VerticalOffset);
            snapshot.RelayoutChangedBlocks(
                dirtyBlockIndices,
                (float)Math.Max(50, ActualWidth),
                CancellationToken.None);
            if (!ReferenceEquals(snapshot, _snapshot))
                return;

            _appliedLazyLayoutRevision = snapshot.LayoutRevision;
            ApplySnapshotSize(snapshot);
            RestoreScrollAnchor(snapshot, anchor);
            RebuildRealizationPlans(snapshot, preserveRealized: true);
            _focusableItems = snapshot.CollectFocusableItems();
            RealizeVisibleEmbeds();
            ScheduleVisibleCodeBlockHighlighting();
            UpdateFocusRing();
            RefreshInteractiveTextAdornerAfterLayoutChange();
            InvalidateCanvas();
            InvalidateAutomationLayout();
        }
        catch (OperationCanceledException)
        {
            // A concurrent full rebuild owns the next committed layout.
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine($"[MarkdownRendererControl] image relayout failed: {ex}");
            RequestRebuild();
        }
    }

    private void InvalidateAutomationLayout()
    {
        // Custom-drawn descendants do not receive XAML layout notifications of
        // their own. Refresh an existing document peer after image reflow so UIA
        // clients see the same bounds as the newly painted snapshot immediately.
        FrameworkElementAutomationPeer.FromElement(this)?.InvalidatePeer();
    }

    private void OnTaskCommandAvailabilityChanged()
    {
        if (_isDisposed || _isUnloaded || _taskCommandAvailabilityRefreshQueued)
            return;

        _taskCommandAvailabilityRefreshQueued = true;
        if (!DispatcherQueue.TryEnqueue(RefreshTaskCommandAvailability))
            _taskCommandAvailabilityRefreshQueued = false;
    }

    private void RefreshTaskCommandAvailability()
    {
        _taskCommandAvailabilityRefreshQueued = false;
        if (_isDisposed || _isUnloaded || _snapshot is not { } snapshot)
            return;

        Layout.FocusableItem? focusedIdentity = GetCurrentFocusableIdentity();
        _focusableItems = snapshot.CollectFocusableItems();
        _focusedItemIndex = focusedIdentity is { } identity
            ? FindFocusableItemIndex(_focusableItems, identity)
            : -1;
        InvalidateAutomationLayout();
    }

    private void OnRegionsInvalidated(CanvasVirtualControl sender, CanvasRegionsInvalidatedEventArgs args)
    {
        if (_isDisposed || _isUnloaded || !IsLoaded || !ReferenceEquals(sender, _canvas))
            return;

        var performance = MarkdownPerformanceEventSource.Log;
        bool measure = performance.IsMeasurementEnabled();
        long started = measure ? Stopwatch.GetTimestamp() : 0;
        long allocatedBefore = measure ? GC.GetAllocatedBytesForCurrentThread() : 0;
        try
        {
            PaintInvalidatedRegions(sender, args);
        }
        catch (Exception ex) when (ShouldIgnoreShutdownException(ex))
        {
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] ignored canvas paint during shutdown: {ex.GetType().Name} {GraphicsDeviceErrors.FormatHResult(ex.HResult)}");
        }
        finally
        {
            if (measure)
            {
                long allocated = Math.Max(
                    0,
                    GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
                performance.PaintWork(
                    MarkdownPerformanceEventSource.GetLogicalFrameId(),
                    Stopwatch.GetTimestamp() - started,
                    allocated);
            }
        }
    }

    private void PaintInvalidatedRegions(CanvasVirtualControl sender, CanvasRegionsInvalidatedEventArgs args)
    {
        var performance = MarkdownPerformanceEventSource.Log;
        bool measure = performance.IsMeasurementEnabled();
        long allocationCheckpoint = measure ? GC.GetAllocatedBytesForCurrentThread() : 0;
        QueueLazyLayoutForViewport();
        long schedulingBytes = measure
            ? Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - allocationCheckpoint)
            : 0;
        long platformSessionBytes = 0;
        long snapshotPaintBytes = 0;
        long interactivePaintBytes = 0;

        if (_snapshot is not { } snapshot) return;
        var frame = ShakeLogger.NextFrame();
        int regionCount = 0;
        bool paintedRegion = false;
        foreach (var region in args.InvalidatedRegions)
        {
            regionCount++;
            if (ShakeLogger.IsEnabled)
                ShakeLogger.LogPaint(
                    "region", regionCount, region.X, region.Y, region.Width, region.Height);
            try
            {
                long regionStart = measure ? GC.GetAllocatedBytesForCurrentThread() : 0;
                // The tile itself is the authoritative paint band; the ScrollViewer
                // viewport can lag behind CanvasVirtualControl's region requests.
                QueueLazyLayoutForPaintRegion(region);
                if (!snapshot.TryBeginPaint())
                {
                    // Do not create or clear the tile while background realization
                    // owns the layout. Completion invalidates the canvas, so the
                    // existing pixels remain visible without blocking this frame.
                    continue;
                }

                try
                {
                    long afterSetup = regionStart;
                    long afterSnapshot = regionStart;
                    long afterInteractive = regionStart;
                    using (var ds = sender.CreateDrawingSession(region))
                    {
                        // Clear to the theme-appropriate background color so that switching between
                        // light and dark mode (or any theme change) fully overwrites old tile content.
                        // We use an opaque theme color rather than Colors.Transparent because
                        // CanvasVirtualControl may not alpha-composite with the XAML compositor
                        // depending on the DirectX swap-chain configuration of the platform; on
                        // such configurations transparent pixels show as black rather than letting
                        // the XAML background show through.
                        ds.Clear(_canvasBackground);
                        // Force grayscale text anti-aliasing. ClearType is colour-aware:
                        // the same glyph rendered onto a white background versus an
                        // alpha-blended selection-tinted background produces subtly
                        // different sub-pixel RGB values. Switching to grayscale makes
                        // glyph edges background-independent.
                        ds.TextAntialiasing = Microsoft.Graphics.Canvas.Text.CanvasTextAntialiasing.Grayscale;
                        if (measure)
                            afterSetup = GC.GetAllocatedBytesForCurrentThread();
                        snapshot.Paint(ds, region);
                        if (measure)
                            afterSnapshot = GC.GetAllocatedBytesForCurrentThread();
                        // Selection/link state is rendered by the dedicated
                        // viewport-bounded image-source adorner. Never bake it
                        // into document tiles: changing a selection must not
                        // invalidate and repaint immutable document content.
                        afterInteractive = afterSnapshot;
                        paintedRegion = true;
                    }
                    if (measure)
                    {
                        long afterDispose = GC.GetAllocatedBytesForCurrentThread();
                        platformSessionBytes += Math.Max(0, afterSetup - regionStart) +
                                                Math.Max(0, afterDispose - afterInteractive);
                        snapshotPaintBytes += Math.Max(0, afterSnapshot - afterSetup);
                        interactivePaintBytes += Math.Max(0, afterInteractive - afterSnapshot);
                    }
                }
                finally
                {
                    snapshot.EndPaint();
                }
            }
            catch (Exception ex) when (GraphicsDeviceErrors.IsDeviceLost(ex))
            {
                HandleCanvasDeviceLost(ex);
                return;
            }
        }
        _canvasDeviceRecoveryAttempt = 0;
        _canvasDeviceRecoveryQueued = false;
        if (paintedRegion &&
            _snapshotGeneration == _pipelineGeneration &&
            _snapshotPipelineStartTimestamp != 0)
        {
            long pipelineStartTimestamp = _snapshotPipelineStartTimestamp;
            long sourceUtf16Bytes = _snapshotSourceUtf16Bytes;
            long generation = _snapshotGeneration;
            _snapshotPipelineStartTimestamp = 0;
            MarkdownPerformanceEventSource.Log.FirstViewportPresented(
                generation,
                sourceUtf16Bytes,
                Stopwatch.GetTimestamp() - pipelineStartTimestamp);
        }
        if (ShakeLogger.IsEnabled)
            ShakeLogger.Log("frame-end",
                $"frame={frame} regions={regionCount} hovered={(_lastHoveredRun is null ? "null" : _lastHoveredRun.GetType().Name)} dragging={_selectionAnchor is not null}");
        if (measure)
        {
            performance.PaintAllocationBreakdown(
                MarkdownPerformanceEventSource.GetLogicalFrameId(),
                schedulingBytes,
                platformSessionBytes,
                snapshotPaintBytes,
                interactivePaintBytes);
        }
    }

    private async Task RunImageRelayoutAsync(
        LayoutSnapshot snapshot,
        int[] changed,
        float width,
        (int BlockIndex, double OffsetFromTop)? anchor,
        long generation,
        CancellationTokenSource cancellation,
        Microsoft.UI.Dispatching.DispatcherQueue? dispatcher)
    {
        Exception? failure = null;
        long started = MarkdownPerformanceEventSource.Log.IsMeasurementEnabled()
            ? Stopwatch.GetTimestamp()
            : 0;
        try
        {
            await Task.Run(
                () => snapshot.RelayoutChangedBlocks(changed, width, cancellation.Token),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        if (started != 0)
            MarkdownPerformanceEventSource.Log.ResourceWork(
                3, Stopwatch.GetTimestamp() - started, changed.Length);

        if (dispatcher is null || !dispatcher.TryEnqueue(() =>
                CompleteImageRelayout(snapshot, anchor, generation, cancellation, failure)))
        {
            cancellation.Dispose();
        }
    }

    private void CompleteImageRelayout(
        LayoutSnapshot snapshot,
        (int BlockIndex, double OffsetFromTop)? anchor,
        long generation,
        CancellationTokenSource cancellation,
        Exception? failure)
    {
        bool current = ReferenceEquals(_imageRelayoutCts, cancellation);
        long started = MarkdownPerformanceEventSource.Log.IsMeasurementEnabled()
            ? Stopwatch.GetTimestamp()
            : 0;
        if (current)
        {
            _imageRelayoutCts = null;
            _imageRelayoutTask = null;
            _imageRelayoutActive = false;
        }

        bool canceled = cancellation.IsCancellationRequested;
        cancellation.Dispose();
        if (current && !_isDisposed && !_isUnloaded && !canceled &&
            generation == _pipelineGeneration && ReferenceEquals(snapshot, _snapshot))
        {
            if (failure is not null)
            {
                MarkdownDiagnostics.WriteLine($"[MarkdownRendererControl] image relayout failed: {failure}");
                RequestRebuild();
            }
            else
            {
                _appliedLazyLayoutRevision = snapshot.LayoutRevision;
                ApplySnapshotSize(snapshot);
                RestoreScrollAnchor(snapshot, anchor);
                RebuildRealizationPlans(snapshot, preserveRealized: true);
                _focusableItems = snapshot.CollectFocusableItems();
                RealizeVisibleEmbeds();
                ScheduleVisibleCodeBlockHighlighting();
                UpdateFocusRing();
                RefreshInteractiveTextAdornerAfterLayoutChange();
                InvalidateCanvas();
                InvalidateAutomationLayout();
            }
        }

        if (current && _pendingImageRelayoutBlockIndices.Count > 0 && !_isDisposed && !_isUnloaded)
        {
            int next = -1;
            foreach (int pending in _pendingImageRelayoutBlockIndices)
            {
                next = pending;
                break;
            }
            if (next >= 0)
                QueueImageRelayout(next);
        }
        if (started != 0)
            MarkdownPerformanceEventSource.Log.ResourceWork(
                4, Stopwatch.GetTimestamp() - started, 1);
    }

    /// <summary>
    /// Paints the current realized viewport through the production snapshot
    /// renderer for deterministic visual-audit evidence. This deliberately
    /// bypasses desktop capture, which cannot see Win2D/DirectComposition
    /// surfaces in several CI, RDP, and headless configurations.
    /// </summary>
    internal async Task<(int Width, int Height, double DocumentTop)> CaptureAuditViewportAsync(
        string? outputPath,
        bool save)
    {
        if (_isDisposed || _snapshot is null)
            throw new InvalidOperationException("The Markdown renderer has no active layout snapshot.");

        // Lazy layout mutation and audit painting share the snapshot lock. A
        // large document can legitimately hold it for longer than a single UI
        // frame, so wait asynchronously for a current snapshot instead of
        // turning normal background realization into an audit failure. Re-read
        // _snapshot on every attempt because a relayout may retire and replace
        // the original candidate while this method is yielding.
        LayoutSnapshot? snapshot = null;
        long acquireStarted = Stopwatch.GetTimestamp();
        while (!_isDisposed && Stopwatch.GetElapsedTime(acquireStarted) < TimeSpan.FromSeconds(5))
        {
            LayoutSnapshot? candidate = _snapshot;
            if (candidate is not null && candidate.TryBeginPaint())
            {
                snapshot = candidate;
                break;
            }

            await Task.Delay(16);
        }

        if (snapshot is null)
            throw new InvalidOperationException("The Markdown layout remained busy while capturing audit evidence.");

        TryGetViewport(out double top, out double viewportHeight, out double viewportWidth);
        int width = Math.Max(1, checked((int)Math.Ceiling(viewportWidth)));
        int height = Math.Max(1, checked((int)Math.Ceiling(viewportHeight)));
        var viewport = new Rect(0, Math.Max(0, top), width, height);

        try
        {
            using var target = new CanvasRenderTarget(
                _canvas?.Device ?? CanvasDevice.GetSharedDevice(),
                width,
                height,
                96);
            using (CanvasDrawingSession drawingSession = target.CreateDrawingSession())
            {
                drawingSession.Clear(_canvasBackground);
                drawingSession.TextAntialiasing = Microsoft.Graphics.Canvas.Text.CanvasTextAntialiasing.Grayscale;
                drawingSession.Transform = Matrix3x2.CreateTranslation(0, (float)-viewport.Top);
                snapshot.Paint(drawingSession, viewport);
            }

            if (save)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
                string fullPath = System.IO.Path.GetFullPath(outputPath);
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
                using var stream = new InMemoryRandomAccessStream();
                await target.SaveAsync(stream, CanvasBitmapFileFormat.Png, 1f);
                if (stream.Size > int.MaxValue)
                    throw new InvalidOperationException("The Markdown audit tile exceeds the managed evidence budget.");
                stream.Seek(0);
                using var reader = new DataReader(stream.GetInputStreamAt(0));
                uint byteLength = checked((uint)stream.Size);
                uint loaded = await reader.LoadAsync(byteLength);
                if (loaded != byteLength)
                    throw new System.IO.EndOfStreamException("The Markdown audit tile ended before its declared size.");
                byte[] bytes = new byte[checked((int)byteLength)];
                reader.ReadBytes(bytes);
                await System.IO.File.WriteAllBytesAsync(fullPath, bytes);
            }

            return (width, height, viewport.Top);
        }
        finally
        {
            snapshot.EndPaint();
        }
    }

    private void HandleCanvasDeviceLost(Exception exception)
    {
        ReleaseSelectionAdornerSurface();
        try
        {
            if (_canvas?.Device is { } device)
                Layout.Boxes.ImageBox.ReleaseDeviceResources(device);
        }
        catch (Exception releaseException)
        {
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] image device-resource release failed: {releaseException.Message}");
        }

        if (_canvasDeviceRecoveryQueued || _isUnloaded)
            return;

        _canvasDeviceRecoveryQueued = true;
        _canvasDeviceRecoveryAttempt = Math.Min(_canvasDeviceRecoveryAttempt + 1, 6);
        int delayMs = Math.Min(5000, 250 << (_canvasDeviceRecoveryAttempt - 1));

        MarkdownDiagnostics.WriteLine(
            "[MarkdownRendererControl] Win2D device lost while painting; " +
            $"HRESULT={GraphicsDeviceErrors.FormatHResult(exception.HResult)}. " +
            $"Retrying canvas rebuild in {delayMs}ms.");

        var dispatcher = DispatcherQueue;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(delayMs).ConfigureAwait(false); }
            catch { return; }

            dispatcher?.TryEnqueue(() =>
            {
                if (_isUnloaded) return;

                _canvasDeviceRecoveryQueued = false;
                RequestRebuild();
                InvalidateCanvas();
                InvalidateSelectionAdorner();
            });
        });
    }

    private void OnCanvasCreateResources(
        CanvasVirtualControl sender,
        Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesEventArgs args)
    {
        if (_isDisposed || _isUnloaded || !ReferenceEquals(sender, _canvas))
            return;

        // Initial resource creation can be deferred until the first input frame in
        // packaged WinUI. Rebuilding there resets pointer capture and selection
        // state, which makes the first click/drag flash and get swallowed. Layout
        // uses CanvasDevice.GetSharedDevice(), so initial creation needs no rebuild.
        // Later resource creation means device/DPI changed, so rebuild layouts.
        if (args.Reason != Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesReason.FirstTime)
            RequestRebuild();
    }

    // ---- Input ----

    private static bool IsPrecisePointer(PointerInput e)
        => MarkdownPointerGesturePolicy.BeginsImmediateSelection(
            GetPointerModality(e.Pointer.PointerDeviceType));

    private static MarkdownPointerModality GetPointerModality(
        Microsoft.UI.Input.PointerDeviceType deviceType)
        => deviceType switch
        {
            Microsoft.UI.Input.PointerDeviceType.Mouse => MarkdownPointerModality.Mouse,
            Microsoft.UI.Input.PointerDeviceType.Touch => MarkdownPointerModality.Touch,
            Microsoft.UI.Input.PointerDeviceType.Pen => MarkdownPointerModality.Pen,
            _ => MarkdownPointerModality.Unknown,
        };

    private bool IsActivePointerSessionEvent(PointerInput e)
        => _pointerSession.IsActive && _pointerSession.PointerId == e.Pointer.PointerId;

    private void ProcessPointerWheelChanged(ref PointerInput e)
    {
        if (_snapshot is not { } snapshot || _canvas is null)
            return;

        var point = e.Point;
        bool horizontalWheel = point.Properties.IsHorizontalMouseWheel;
        bool shift = (e.Modifiers & VirtualKeyModifiers.Shift) != 0;
        if (!horizontalWheel && !shift)
            return;

        if (!snapshot.TryGetHorizontalOverflow(point.Position, out var target) || target is null)
            return;

        int wheelDelta = point.Properties.MouseWheelDelta;
        if (wheelDelta == 0)
            return;

        double oldOffset = target.HorizontalOffset;
        double delta = -(wheelDelta / 120.0) * 64.0;
        if (target.IsRightToLeft)
            delta = -delta;

        if (!snapshot.TryScrollHorizontal(target, delta))
            return;

        RefreshHorizontalOverflowVisualState(target, oldOffset);
        e.Handled = true;
    }

    private bool TryBeginHorizontalOverflowPointer(ref PointerInput e)
    {
        if (_snapshot is not { } snapshot || _canvas is null)
            return false;

        var point = e.Point;
        if (!point.IsInContact ||
            !snapshot.TryGetHorizontalOverflow(point.Position, out var target) ||
            target is null)
        {
            return false;
        }

        Rect track = target.HorizontalScrollTrackBounds;
        bool precise = IsPrecisePointer(e);
        if (precise && !track.Contains(point.Position))
            return false;

        _horizontalOverflowPointerId = e.Pointer.PointerId;
        _horizontalOverflowSnapshot = snapshot;
        _horizontalOverflowTarget = target;
        _horizontalOverflowPressPoint = point.Position;
        _horizontalOverflowInitialOffset = target.HorizontalOffset;
        _horizontalOverflowCaptured = false;
        _horizontalOverflowThumbDrag = precise;

        if (!precise)
        {
            // A touch contact starts in a pending state. Do not capture or mark
            // handled until horizontal movement wins the gesture threshold, so
            // the ancestor ScrollViewer remains free to own vertical panning.
            return true;
        }

        Rect thumb = target.HorizontalScrollThumbBounds;
        double oldOffset = target.HorizontalOffset;
        if (!thumb.Contains(point.Position))
        {
            SetHorizontalOffsetFromTrackPoint(snapshot, target, point.Position.X, centerThumb: true);
            thumb = target.HorizontalScrollThumbBounds;
        }
        _horizontalOverflowThumbGrabOffset = Math.Clamp(
            point.Position.X - thumb.Left,
            0,
            Math.Max(0, thumb.Width));

        if (!CapturePointerForInput(_canvas, e))
        {
            ClearHorizontalOverflowPointerState();
            return false;
        }

        _horizontalOverflowCaptured = true;
        if (Math.Abs(target.HorizontalOffset - oldOffset) > 0.1)
            RefreshHorizontalOverflowVisualState(target, oldOffset);
        e.Handled = true;
        return true;
    }

    private bool HandleHorizontalOverflowPointerMoved(ref PointerInput e, Point point)
    {
        if (_horizontalOverflowPointerId == 0 ||
            _horizontalOverflowPointerId != e.Pointer.PointerId ||
            _horizontalOverflowSnapshot is not { } snapshot ||
            _horizontalOverflowTarget is not { } target ||
            !ReferenceEquals(snapshot, _snapshot))
        {
            return false;
        }

        if (_horizontalOverflowThumbDrag)
        {
            double oldOffset = target.HorizontalOffset;
            SetHorizontalOffsetFromTrackPoint(
                snapshot,
                target,
                point.X - _horizontalOverflowThumbGrabOffset,
                centerThumb: false);
            if (Math.Abs(target.HorizontalOffset - oldOffset) > 0.1)
                RefreshHorizontalOverflowVisualState(target, oldOffset);
            e.Handled = true;
            return true;
        }

        double dx = point.X - _horizontalOverflowPressPoint.X;
        double dy = point.Y - _horizontalOverflowPressPoint.Y;
        if (!_horizontalOverflowCaptured)
        {
            MarkdownPanGestureDecision decision =
                MarkdownPointerGesturePolicy.ClassifyHorizontalPan(dx, dy);
            if (decision == MarkdownPanGestureDecision.Pending)
                return false;

            if (decision == MarkdownPanGestureDecision.YieldToAncestor)
            {
                ClearHorizontalOverflowPointerState();
                return false;
            }

        if (_canvas is null || !CapturePointerForInput(_canvas, e))
            {
                ClearHorizontalOverflowPointerState();
                return false;
            }
            _horizontalOverflowCaptured = true;
            SuppressTouchTap(point);
        }

        double oldTouchOffset = target.HorizontalOffset;
        double requested = target.IsRightToLeft
            ? _horizontalOverflowInitialOffset + dx
            : _horizontalOverflowInitialOffset - dx;
        if (snapshot.TrySetHorizontalOffset(target, requested))
            RefreshHorizontalOverflowVisualState(target, oldTouchOffset);
        e.Handled = true;
        return true;
    }

    private bool HandleHorizontalOverflowPointerReleased(ref PointerInput e)
    {
        if (_horizontalOverflowPointerId == 0 ||
            _horizontalOverflowPointerId != e.Pointer.PointerId)
        {
            return false;
        }

        bool captured = _horizontalOverflowCaptured;
        if (captured && _canvas is not null)
            SuppressTouchTap(e.Point.Position);
        ClearHorizontalOverflowPointerState();
        if (captured && _canvas is not null)
        {
            e.Handled = true;
            _canvas.ReleasePointerCapture(e.Pointer);
            ResumePendingRebuildAfterPointerInteraction();
        }
        return captured;
    }

    private bool HandleHorizontalOverflowPointerCanceled(ref PointerInput e)
    {
        if (_horizontalOverflowPointerId == 0 ||
            _horizontalOverflowPointerId != e.Pointer.PointerId)
        {
            return false;
        }

        bool captured = _horizontalOverflowCaptured;
        ClearHorizontalOverflowPointerState();
        if (captured)
        {
            e.Handled = true;
            ResumePendingRebuildAfterPointerInteraction();
        }
        return captured;
    }

    private static void SetHorizontalOffsetFromTrackPoint(
        LayoutSnapshot snapshot,
        IHorizontalOverflowBox target,
        double x,
        bool centerThumb)
    {
        Rect track = target.HorizontalScrollTrackBounds;
        Rect thumb = target.HorizontalScrollThumbBounds;
        if (track.IsEmpty || thumb.IsEmpty)
            return;

        double thumbLeft = centerThumb ? x - thumb.Width / 2.0 : x;
        double travel = Math.Max(0, track.Width - thumb.Width);
        if (travel <= 0)
            return;

        double physicalFraction = Math.Clamp((thumbLeft - track.Left) / travel, 0, 1);
        double logicalFraction = target.IsRightToLeft
            ? 1.0 - physicalFraction
            : physicalFraction;
        double maximum = Math.Max(0, target.HorizontalExtent - target.HorizontalViewport);
        snapshot.TrySetHorizontalOffset(target, logicalFraction * maximum);
    }

    private void RefreshHorizontalOverflowVisualState(
        IHorizontalOverflowBox target,
        double oldOffset)
    {
        double logicalDelta = target.HorizontalOffset - oldOffset;
        if (Math.Abs(logicalDelta) <= 0.1)
            return;

        double physicalDelta = target.IsRightToLeft
            ? logicalDelta
            : -logicalDelta;
        bool movedEmbed = false;
        foreach (var plan in _embedPlans)
        {
            if (plan is not InlineEmbedPlan inline ||
                !HorizontalOverflowContains(target, inline.Icb))
            {
                continue;
            }

            inline.Rect = new Rect(
                inline.Rect.X + physicalDelta,
                inline.Rect.Y,
                inline.Rect.Width,
                inline.Rect.Height);
            inline.UpdatePlacement();
            movedEmbed = true;
        }

        if (movedEmbed)
            RealizeVisibleEmbeds();
        if (_selection.IsActive)
            UpdateSelectionOverlay();
        if (_focusedItemIndex >= 0)
            UpdateFocusRing();
        QueueHorizontalOverflowAutomationNotification(target, oldOffset);

        if (_canvas is { } canvas)
        {
            Rect viewport = target.HorizontalViewportBounds;
            Rect track = target.HorizontalScrollTrackBounds;
            if (MarkdownCanvasInvalidationPolicy.TryClipToCanvas(
                    new Rect(
                        viewport.X - 1,
                        viewport.Y - 1,
                        viewport.Width + 2,
                        Math.Max(viewport.Bottom, track.Bottom) - viewport.Y + 2),
                    canvas.Width,
                    canvas.Height,
                    out Rect invalidation))
            {
                canvas.Invalidate(invalidation);
            }
        }
    }

    private void QueueHorizontalOverflowAutomationNotification(
        IHorizontalOverflowBox target,
        double oldOffset)
    {
        if (!AutomationPeer.ListenerExists(AutomationEvents.PropertyChanged) ||
            _isDisposed ||
            _isUnloaded ||
            _snapshot is not { } snapshot ||
            !snapshot.SemanticDocument.TryGetHorizontalOverflowNode(target, out _))
        {
            return;
        }

        double oldPhysicalPercent = MarkdownHorizontalScrollPolicy.GetPhysicalPercent(
            oldOffset,
            target.HorizontalExtent,
            target.HorizontalViewport,
            target.IsRightToLeft);
        double newPhysicalPercent = MarkdownHorizontalScrollPolicy.GetPhysicalPercent(
            target.HorizontalOffset,
            target.HorizontalExtent,
            target.HorizontalViewport,
            target.IsRightToLeft);
        if (Math.Abs(newPhysicalPercent - oldPhysicalPercent) <= 0.0001)
            return;

        if (_pendingHorizontalOverflowAutomationChanges.TryGetValue(
                target,
                out PendingHorizontalOverflowAutomationChange pending) &&
            ReferenceEquals(pending.Snapshot, snapshot))
        {
            _pendingHorizontalOverflowAutomationChanges[target] = pending with
            {
                NewPhysicalPercent = newPhysicalPercent,
            };
        }
        else
        {
            _pendingHorizontalOverflowAutomationChanges[target] = new(
                snapshot,
                oldPhysicalPercent,
                newPhysicalPercent);
        }

        if (_horizontalOverflowAutomationEventQueued)
            return;

        _horizontalOverflowAutomationEventQueued = true;
        Microsoft.UI.Dispatching.DispatcherQueue? dispatcher = DispatcherQueue;
        if (dispatcher is null || !dispatcher.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                ProcessQueuedHorizontalOverflowAutomationNotifications))
        {
            _horizontalOverflowAutomationEventQueued = false;
            _pendingHorizontalOverflowAutomationChanges.Clear();
        }
    }

    private void ProcessQueuedHorizontalOverflowAutomationNotifications()
    {
        _horizontalOverflowAutomationEventQueued = false;
        var changes = new KeyValuePair<IHorizontalOverflowBox, PendingHorizontalOverflowAutomationChange>[
            _pendingHorizontalOverflowAutomationChanges.Count];
        ((ICollection<KeyValuePair<IHorizontalOverflowBox, PendingHorizontalOverflowAutomationChange>>)
            _pendingHorizontalOverflowAutomationChanges).CopyTo(changes, 0);
        _pendingHorizontalOverflowAutomationChanges.Clear();

        if (_isDisposed || _isUnloaded || _snapshot is not { } currentSnapshot)
            return;

        MarkdownAutomationPeer? peer =
            FrameworkElementAutomationPeer.FromElement(this) as MarkdownAutomationPeer;
        if (peer is null)
            return;

        foreach (KeyValuePair<IHorizontalOverflowBox, PendingHorizontalOverflowAutomationChange> entry in changes)
        {
            PendingHorizontalOverflowAutomationChange change = entry.Value;
            if (!ReferenceEquals(change.Snapshot, currentSnapshot) ||
                !currentSnapshot.SemanticDocument.TryGetHorizontalOverflowNode(entry.Key, out _))
            {
                continue;
            }

            peer.NotifyHorizontalOverflowScrolled(
                entry.Key,
                change.OldPhysicalPercent,
                change.NewPhysicalPercent);
        }
    }

    internal void ScrollHorizontalOverflowForAutomation(
        IHorizontalOverflowBox target,
        ScrollAmount amount)
    {
        if (_snapshot is not { } snapshot)
            return;

        double physicalDelta = amount switch
        {
            ScrollAmount.LargeDecrement => -Math.Max(48, target.HorizontalViewport * 0.9),
            ScrollAmount.SmallDecrement => -48,
            ScrollAmount.SmallIncrement => 48,
            ScrollAmount.LargeIncrement => Math.Max(48, target.HorizontalViewport * 0.9),
            _ => 0,
        };
        double logicalDelta = MarkdownHorizontalScrollPolicy.GetLogicalDelta(
            physicalDelta,
            target.IsRightToLeft);
        double oldOffset = target.HorizontalOffset;
        if (snapshot.TryScrollHorizontal(target, logicalDelta))
            RefreshHorizontalOverflowVisualState(target, oldOffset);
    }

    internal void SetHorizontalOverflowPercentForAutomation(
        IHorizontalOverflowBox target,
        double percent)
    {
        if (_snapshot is not { } snapshot || !double.IsFinite(percent))
            return;

        double oldOffset = target.HorizontalOffset;
        double logicalOffset = MarkdownHorizontalScrollPolicy.GetLogicalOffset(
            percent,
            target.HorizontalExtent,
            target.HorizontalViewport,
            target.IsRightToLeft);
        if (snapshot.TrySetHorizontalOffset(target, logicalOffset))
            RefreshHorizontalOverflowVisualState(target, oldOffset);
    }

    private static bool HorizontalOverflowContains(
        IHorizontalOverflowBox target,
        Layout.Boxes.InlineContainerBox box)
    {
        if (target is Layout.Boxes.CodeBlockBox code)
        {
            foreach (var chunk in code.Chunks)
            {
                if (ReferenceEquals(chunk, box))
                    return true;
            }
            return false;
        }

        if (target is Layout.Boxes.TableBox table)
        {
            foreach (var cell in table.GetCellBoxes())
            {
                if (ReferenceEquals(cell, box))
                    return true;
            }
        }
        return false;
    }

    private void ClearHorizontalOverflowPointerState()
    {
        _horizontalOverflowPointerId = 0;
        _horizontalOverflowSnapshot = null;
        _horizontalOverflowTarget = null;
        _horizontalOverflowPressPoint = default;
        _horizontalOverflowInitialOffset = 0;
        _horizontalOverflowThumbGrabOffset = 0;
        _horizontalOverflowCaptured = false;
        _horizontalOverflowThumbDrag = false;
    }

    private void ProcessPointerPressed(ref PointerInput e)
    {
        if (_snapshot is null || _canvas is null)
            return;

        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch)
        {
            Rect contact = e.Point.Properties.ContactRect;
            _touchSelection.BeginContact(
                e.Pointer.PointerId,
                new TouchContactGeometry(contact.X, contact.Y, contact.Width, contact.Height));
        }

        if (TryBeginHorizontalOverflowPointer(ref e))
        {
            if (e.Handled)
                return;
            if (!IsPrecisePointer(e))
                return;
        }
        MarkdownPointerModality modality = GetPointerModality(e.Pointer.PointerDeviceType);
        if (!MarkdownPointerGesturePolicy.BeginsImmediateSelection(modality))
            return;

        // Only process left (primary) button presses; right-clicks are handled by
        // OnRightTapped and must not affect the multi-click counter or selection anchor.
        var currentPoint = e.Point;
        if (!MarkdownPointerGesturePolicy.IsPrimarySelectionPress(
                modality,
                currentPoint.IsInContact,
                currentPoint.Properties.IsLeftButtonPressed,
                currentPoint.Properties.IsBarrelButtonPressed))
            return;
        var pt = currentPoint.Position;
        _touchSelection.Reset();
        UpdateSelectionHandles();
        TryGetViewport(out double pointerViewportTop, out _, out _);
        _lastSelectionPointerViewportY = pt.Y - pointerViewportTop;
        RememberFocusResumePoint(pt);

        // Pressing renderer chrome (code-copy action) must not create or clear
        // selection; let the native button handle the click.
        if (IsPointOverCodeBlockAction(pt))
        {
            _consecutiveClickCount = 0;
            _lastPressTickMs = 0;
            _lastPressPoint = default;
            return;
        }

        HideAbbreviationTooltip();

        // Pressing *on* a hosted inline embed must NOT start a selection.
        // The embed is a real WinUI element layered above the canvas — its
        // own pointer-pressed handler must run (Button click, TextBox focus,
        // …).  Returning here without setting _selectionAnchor or capturing
        // the pointer lets XAML's normal pointer routing deliver the event
        // to the embedded element.
        if (IsPointOverEmbed(pt))
        {
            // A press on an embedded control breaks any ongoing double/triple-click
            // sequence; reset timing state so the next text press starts fresh.
            _consecutiveClickCount = 0;
            _lastPressTickMs = 0;
            _lastPressPoint  = default;
            // Clear any prior selection so the user gets visual feedback that
            // the click isn't a new selection start.
            if (!_selection.Range.IsEmpty)
            {
                _selection.Clear();
                // No _canvas.Invalidate() needed — overlay cleared by Changed handler.
            }
            // Also dismiss keyboard focus ring on click over embed.
            if (_focusedItemIndex >= 0)
            {
                _pendingLazyFocus = null;
                _focusedItemIndex = -1;
                UpdateFocusRing();
            }
            return;
        }

        // Dismiss any keyboard focus ring — mouse clicks and keyboard nav are
        // separate modalities; clicking anywhere in the document should clear
        // the ring so the focus indicator doesn't linger after the user
        // switches from keyboard to mouse.
        if (_focusedItemIndex >= 0)
        {
            _pendingLazyFocus = null;
            _focusedItemIndex = -1;
            UpdateFocusRing();
        }

        // Track consecutive clicks for word (double) and line (triple) selection.
        long nowMs = e.TickCount;
        double dx = pt.X - _lastPressPoint.X;
        double dy = pt.Y - _lastPressPoint.Y;
        bool sameSpot = dx * dx + dy * dy < 16; // 4px radius
        bool withinTime = (nowMs - _lastPressTickMs) <= _doubleClickTimeMs;
        if (sameSpot && withinTime)
        {
            _consecutiveClickCount++;
            if (_consecutiveClickCount > 3) _consecutiveClickCount = 3; // cap: 4+ repeats = line-select
        }
        else
            _consecutiveClickCount = 1;

        FocusRendererForPointerInteraction();
        if (_snapshot.HitTest(pt, out var pos))
        {
            if (ShakeLogger.IsEnabled)
            {
                ShakeLogger.Log(
                    "ptr-press-hit",
                    $"px={pt.X:F2} py={pt.Y:F2} scroll={pointerViewportTop:F2} " +
                    $"pos=blk{pos.BlockIndex}/inl{pos.InlineIndex}/c{pos.CharacterOffset}");
            }
            // Advance clock/position only on successful text hits so a miss in the
            // same spot doesn't corrupt the double/triple-click timing window.
            _lastPressTickMs = nowMs;
            _lastPressPoint  = pt;
            _pointerSession = new PointerSession(e.Pointer.PointerId, IsPrimary: true);

            if (!IsSelectionEnabled)
            {
                // Selection is disabled but links must still work: capture the pointer
                // so OnPointerReleased fires and can raise LinkClick.
                // Also ensure _clickMode is reset so the link-click guard in
                // OnPointerReleased is not stale from a previous double/triple-click
                // sequence made while IsSelectionEnabled was true.
                _clickMode = ClickMode.Single;
                if (!CapturePointerForInput(_canvas, e)) _pointerSession = default;
                else e.Handled = true;
                return;
            }

            // Always arm the anchor first: this suppresses hover processing
            // in OnPointerMoved during any captured drag (single, double, or triple-click)
            // and prevents a stale anchor from an earlier interaction being reused.
            _selectionAnchor = pos;

            if (_consecutiveClickCount == 3)
            {
                // Triple-click: select the entire block (line).
                _clickMode = ClickMode.Block;
                (_dragAnchorStart, _dragAnchorEnd) = ExpandSelectionToBlock(_snapshot, pos);
                _selection.SetAnchor(_dragAnchorStart);
                _selection.ExtendTo(_dragAnchorEnd);
                // Selection is rendered by the XAML overlay; do not dirty canvas
                // text during mouse-down. Repainting DirectWrite text here causes
                // visible shake on selection starts, especially on the embeds page.
                if (!CapturePointerForInput(_canvas, e)) { _pointerSession = default; _selectionAnchor = null; }
                else e.Handled = true;
                return;
            }
            if (_consecutiveClickCount == 2)
            {
                // Double-click: select the word under the cursor.
                _clickMode = ClickMode.Word;
                (_dragAnchorStart, _dragAnchorEnd) = ExpandSelectionToWord(_snapshot, pos);
                _selection.SetAnchor(_dragAnchorStart);
                _selection.ExtendTo(_dragAnchorEnd);
                // Selection is rendered by the XAML overlay; do not dirty canvas
                // text during mouse-down.
                if (!CapturePointerForInput(_canvas, e)) { _pointerSession = default; _selectionAnchor = null; }
                else e.Handled = true;
                return;
            }
            _clickMode = ClickMode.Single;
            _selection.SetAnchor(pos);
            // Selection is rendered by the XAML overlay; do not dirty canvas text
            // for the empty anchor state.
            bool captured = CapturePointerForInput(_canvas, e);
            if (!captured) { _pointerSession = default; _selectionAnchor = null; }
                else e.Handled = true;
            }
        else if (TryGetVectorPointerState(pt, out bool vectorLinked, out _) && vectorLinked)
        {
            // Linked and Selectable are independent scene semantics. A linked-only
            // item must still receive a complete press/release gesture, but it must
            // not become a text-selection anchor.
            _lastPressTickMs = nowMs;
            _lastPressPoint = pt;
            _clickMode = ClickMode.Single;
            _pointerSession = new PointerSession(e.Pointer.PointerId, IsPrimary: true);
            _selectionAnchor = null;
            _selection.Clear();
            if (!CapturePointerForInput(_canvas, e))
                _pointerSession = default;
            else
                e.Handled = true;
        }
        else
        {
            // HitTest missed (pointer landed on a gap or embed area).
            // Reset click state so this miss isn't counted toward a future
            // double/triple-click; also reset timing so a miss→text sequence
            // never misclassifies as a double-click.
            _consecutiveClickCount = 0;
            _lastPressTickMs = 0;
            _lastPressPoint  = default;
            _selectionAnchor = null; // defensive: clear any stale anchor from a prior capture loss
            _lastSelectionPointerViewportY = double.NaN;
            _selection.Clear();
            // No canvas invalidate: selection clear is overlay-only.
        }
    }

    private void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_canvas is not null && e.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch)
            e.Handled |= DispatchRecognizedGesture(PointerInputKind.Tap, e.GetPosition(_canvas), e.PointerDeviceType);
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_canvas is not null && e.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch)
            e.Handled |= DispatchRecognizedGesture(PointerInputKind.DoubleTap, e.GetPosition(_canvas), e.PointerDeviceType);
    }

    private void ProcessTapped(ref PointerInput e)
    {
        if (_snapshot is null || _canvas is null ||
            e.GestureDevice != Microsoft.UI.Input.PointerDeviceType.Touch)
        {
            return;
        }

        Point point = e.GesturePosition;
        if (IsSuppressedTouchGesture(point))
        {
            e.Handled = true;
            return;
        }

        if (IsPointOverEmbed(point) || IsPointOverCodeBlockAction(point))
            return;

        if (_selection.IsActive && IsPointInsideSelection(point))
        {
            e.Handled = ShowSelectionContextMenu(point, touchSelection: true);
            return;
        }

        if (_selection.IsActive)
            ClearSelectionForExternalInteraction(resetClickTracking: false);

        // Tapped is raised only after WinUI's gesture recognizer has ruled out
        // manipulation. Unlike PointerPressed, this lets an ancestor
        // ScrollViewer retain vertical-pan ownership without a speculative
        // renderer capture.
        RememberFocusResumePoint(point);
        if (ActivateLinkAtPoint(point, MarkdownLinkInputKind.Touch))
            e.Handled = true;
    }

    private void ProcessDoubleTapped(ref PointerInput e)
    {
        if (_snapshot is null || _canvas is null ||
            e.GestureDevice != Microsoft.UI.Input.PointerDeviceType.Touch)
        {
            return;
        }

        Point point = e.GesturePosition;
        if (IsPointOverEmbed(point) || IsPointOverCodeBlockAction(point))
            return;

        ClearHorizontalOverflowPointerState();
        EndCapturedSelectionForContextGesture();
        if (TrySelectWordForTouchContext(point))
        {
            SuppressTouchTap(point);
            e.Handled = true;
        }
    }

    private void OnHolding(object sender, HoldingRoutedEventArgs e)
    {
        if (_canvas is not null && e.HoldingState == Microsoft.UI.Input.HoldingState.Started &&
            e.PointerDeviceType is Microsoft.UI.Input.PointerDeviceType.Touch or Microsoft.UI.Input.PointerDeviceType.Pen)
            e.Handled |= DispatchRecognizedGesture(PointerInputKind.Hold, e.GetPosition(_canvas), e.PointerDeviceType);
    }

    private void ProcessHolding(ref PointerInput e)
    {
        if (_snapshot is null || _canvas is null ||
            e.GestureDevice is not (
                Microsoft.UI.Input.PointerDeviceType.Touch or
                Microsoft.UI.Input.PointerDeviceType.Pen))
        {
            return;
        }

        Point point = e.GesturePosition;
        if (IsPointOverEmbed(point) || IsPointOverCodeBlockAction(point))
            return;

        // A stationary hold has established selection/context intent. Clear a
        // still-pending block-local pan and end any pen-selection capture: the
        // flyout and explicit selection handles own subsequent interaction.
        ClearHorizontalOverflowPointerState();
        EndCapturedSelectionForContextGesture();
        if (TrySelectWordForTouchContext(point))
        {
            SuppressTouchTap(point);
            e.Handled = true;
        }
    }

    private void EndCapturedSelectionForContextGesture()
    {
        if (!_pointerSession.IsActive || _canvas is null)
            return;

        // Clear state before releasing captures because WinUI may synchronously
        // route PointerCaptureLost back into the renderer.
        _pointerSession = default;
        _selectionAnchor = null;
        _lastSelectionPointerViewportY = double.NaN;
        SetSelectionDragShieldActive(false);
        _canvas.ReleasePointerCaptures();
        ResumePendingRebuildAfterPointerInteraction();
    }

    private bool TrySelectWordForTouchContext(Point point)
    {
        if (!IsSelectionEnabled || _snapshot is null ||
            !TryHitTestTouchSelection(point, out DocumentPosition position))
        {
            return false;
        }

        FocusRendererForPointerInteraction();
        _consecutiveClickCount = 0;
        _lastPressTickMs = 0;
        _lastPressPoint = default;
        _selectionAnchor = null;
        _clickMode = ClickMode.Word;
        (_dragAnchorStart, _dragAnchorEnd) = ExpandSelectionToWord(_snapshot, position);
        if (_selection.IsActive)
            _touchSelection.Select();
        UpdateSelectionOverlay();
        return _selection.IsActive;
    }

    private bool TryHitTestTouchSelection(Point gesturePoint, out DocumentPosition position)
    {
        position = default;
        if (_snapshot is null)
            return false;

        if (IsSelectableTouchPoint(gesturePoint, out position))
            return true;

        TouchContactGeometry contact = _touchSelection.Contact;
        if (!_touchSelection.HasContact || contact.Width <= 0 || contact.Height <= 0)
            return false;

        double left = contact.X;
        double top = contact.Y;
        double right = contact.X + contact.Width;
        double bottom = contact.Y + contact.Height;
        Point center = new(contact.CenterX, contact.CenterY);
        double insetX = Math.Min(4, contact.Width / 4);
        double insetY = Math.Min(4, contact.Height / 4);
        return IsSelectableTouchPoint(center, out position) ||
               IsSelectableTouchPoint(new Point(left + insetX, center.Y), out position) ||
               IsSelectableTouchPoint(new Point(right - insetX, center.Y), out position) ||
               IsSelectableTouchPoint(new Point(center.X, top + insetY), out position) ||
               IsSelectableTouchPoint(new Point(center.X, bottom - insetY), out position);
    }

    private bool IsSelectableTouchPoint(Point point, out DocumentPosition position)
    {
        position = default;
        return _snapshot is not null &&
               !IsPointOverEmbed(point) &&
               !IsPointOverCodeBlockAction(point) &&
               _snapshot.HitTest(point, out position);
    }

    private bool IsPointInsideSelection(Point point)
    {
        foreach (Rect rectangle in _selectionAdornerRects)
        {
            if (rectangle.Contains(point))
                return true;
        }

        return false;
    }

    private void SuppressTouchTap(Point point)
    {
        _suppressTouchTapUntilTickMs = Environment.TickCount64 + 1_500;
        _suppressedTouchTapPoint = point;
    }

    private bool IsSuppressedTouchGesture(Point point)
        => IsGestureSuppressed(
            _suppressTouchTapUntilTickMs,
            _suppressedTouchTapPoint,
            point);

    private static bool IsGestureSuppressed(long deadlineTickMs, Point origin, Point point)
    {
        if (deadlineTickMs - Environment.TickCount64 < 0)
            return false;

        double dx = point.X - origin.X;
        double dy = point.Y - origin.Y;
        return dx * dx + dy * dy <= 48 * 48;
    }

    private void FocusRendererForPointerInteraction()
    {
        _suppressNextFocusPromotion = true;
        bool focused = Focus(FocusState.Programmatic);
        if (!focused)
        {
            _suppressNextFocusPromotion = false;
            return;
        }

        DispatcherQueue.TryEnqueue(() => _suppressNextFocusPromotion = false);
    }

    private InlineRun? _lastHoveredRun;
    private Layout.Boxes.InlineContainerBox? _lastHoveredBox; // box that contains _lastHoveredRun; used for targeted canvas invalidation
    private AbbreviationRun? _lastHoveredAbbreviation;
    private ToolTip? _abbreviationToolTip;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _abbreviationTooltipShowTimer;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _abbreviationTooltipHideTimer;
    private AbbreviationRun? _pendingAbbreviationTooltipRun;
    private Rect _pendingAbbreviationTooltipPlacementRect;
    private static readonly TimeSpan AbbreviationTooltipShowDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan AbbreviationTooltipHideDelay = TimeSpan.FromMilliseconds(200);
    // Tracks the ProtectedCursor shape we last set, or null when we have
    // reset to the system default.  Three states:
    //   null            → ProtectedCursor was reset; system default (Arrow) shows.
    //                     Occurs when pointer exits the canvas or is over an embed.
    //   IBeam           → pointer is over selectable text (not a link).
    //   Hand            → pointer is over a link run.
    // We only call ProtectedCursor setter when the desired state changes, but
    // the "null" state is critical: setting ProtectedCursor = null on *this*
    // UserControl means child elements (embeds) can use their own cursors
    // without a parent IBeam overriding them.
    private Microsoft.UI.Input.InputSystemCursorShape? _currentCursorShape;

    private void ProcessPointerMoved(ref PointerInput e)
    {
        if (_snapshot is null || _canvas is null) return;
        var pt = e.Point.Position;

        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch)
        {
            Rect contact = e.Point.Properties.ContactRect;
            _touchSelection.UpdateContact(
                e.Pointer.PointerId,
                new TouchContactGeometry(contact.X, contact.Y, contact.Width, contact.Height));
        }

        if (HandleHorizontalOverflowPointerMoved(ref e, pt))
            return;

        // Drag-select.
        if (_selectionAnchor is not null)
        {
            if (!IsActivePointerSessionEvent(e))
                return;

            e.Handled = true;

            HideAbbreviationTooltip();
            var dragPoint = PrepareSelectionDragPoint(pt);
            // Atomic embed inclusion: when the pointer is inside an inline
            // embed rect during a drag, snap the position to either the
            // start or end of the InlineEmbedRun (whichever side the pointer
            // is closer to).  This treats the embed as a single, indivisible
            // unit of selectable content — the user can never have a
            // selection that ends *halfway through* an embedded button or
            // textbox, which matches how browsers handle <input> /
            // <textarea> inside contenteditable text.
            if (TryHitTestEmbed(dragPoint, out var embedPos))
            {
                if (ShakeLogger.IsEnabled)
                    ShakeLogger.Log("ptr-move-drag-embed",
                        $"px={dragPoint.X:F4} py={dragPoint.Y:F4} pos=blk{embedPos.BlockIndex}/inl{embedPos.InlineIndex}/c{embedPos.CharacterOffset}");
                _selection.ExtendTo(embedPos);
                // No _canvas.Invalidate(): selection is on the XAML overlay.
            }
            else if (_snapshot.HitTestSelectionEndpoint(dragPoint, out var pos))
            {
                if (ShakeLogger.IsEnabled)
                    ShakeLogger.Log("ptr-move-drag",
                        $"px={dragPoint.X:F4} py={dragPoint.Y:F4} pos=blk{pos.BlockIndex}/inl{pos.InlineIndex}/c{pos.CharacterOffset}");
                // For word/block click modes extend selection snapped to the
                // appropriate boundary so dragging after double/triple-click
                // produces word-by-word or block-by-block selection, matching
                // browser / native text editor behaviour.
                if (_clickMode == ClickMode.Word)
                {
                    var icb = FindInlineContainerAt(_snapshot, pos.BlockIndex);
                    if (icb is not null)
                    {
                        var (ws, we) = icb.GetWordBoundaries(pos);
                        // Backward drag: anchor at initial-word end, extend to current-word start.
                        // Forward drag: anchor at initial-word start, extend to current-word end.
                        if (pos.CompareTo(_dragAnchorStart) < 0)
                        { _selection.SetAnchor(_dragAnchorEnd); _selection.ExtendTo(ws); }
                        else
                        { _selection.SetAnchor(_dragAnchorStart); _selection.ExtendTo(we); }
                    }
                    else
                        _selection.ExtendTo(pos); // no ICB (code block/embed) — fall back to char selection
                }
                else if (_clickMode == ClickMode.Block)
                {
                    var icb = FindInlineContainerAt(_snapshot, pos.BlockIndex);
                    if (icb is not null)
                    {
                        var (bs, be) = icb.GetBlockBoundaries();
                        if (pos.CompareTo(_dragAnchorStart) < 0)
                        { _selection.SetAnchor(_dragAnchorEnd); _selection.ExtendTo(bs); }
                        else
                        { _selection.SetAnchor(_dragAnchorStart); _selection.ExtendTo(be); }
                    }
                    else
                        _selection.ExtendTo(pos); // no ICB — fall back to char selection
                }
                else
                    _selection.ExtendTo(pos);
                // No _canvas.Invalidate(): selection is on the XAML overlay.
            }
            // Do NOT update hover state during an active selection drag.
            // Calling SetColor on the CanvasTextLayout (which ApplyHoverColor
            // does whenever HoveredRun changes) causes DirectWrite to
            // invalidate cached glyph metrics, which in turn produces
            // sub-pixel vertical jitter ("selection shake") whenever the
            // pointer crosses run boundaries — most visibly on body text
            // that contains links, because every time the pointer moves
            // between the link run and the surrounding body run the
            // hovered-run identity flips.  Suppressing the toggle here
            // costs nothing because the IBeam cursor and link-hover color
            // aren't relevant while the user is mid-drag selecting.
            return;
        }

        // Touch contacts should pan the containing ScrollViewer. Treating touch
        // like a mouse press can capture the pointer, start text selection, and
        // drive the Win2D selection overlay at touch-scroll frequency.
        if (!IsPrecisePointer(e))
        {
            HideAbbreviationTooltip();
            SetCursorShape(null);
            return;
        }

        // Pointer hovering an inline embed: do nothing.  The embed is its
        // own pointer-event target and its ProtectedCursor (Hand / IBeam /
        // arrow / …) takes effect via XAML's normal pointer routing.  We
        // reset our own ProtectedCursor to null so child elements are not
        // overridden by an IBeam that this UserControl last set.  null
        // means "no cursor override" — XAML walks up the tree and finds
        // nothing, so the system default (Arrow) applies, allowing each
        // embed to set its own cursor if desired.
        if (IsPointOverEmbed(pt))
        {
            HideAbbreviationTooltip();
            // Clear our own link-hover so when the pointer leaves the embed
            // the previous hovered link doesn't appear stuck-on.
            if (_lastHoveredRun is not null)
            {
                // Only the LinkRun case actually mutates visible state (link
                // foreground color); plain TextRun hover changes nothing.
                // Issuing a _canvas.Invalidate for a TextRun→null transition
                // causes a partial tile repaint, and CanvasVirtualControl
                // partial-tile repaiits reveal DirectWrite glyph-position
                // variance at tile boundaries → visible text shake.
                bool wasLink = _lastHoveredRun is LinkRun;
                var boxToInvalidate = _lastHoveredBox;
                foreach (var b in _snapshot.Blocks) ClearHover(b);
                _lastHoveredRun = null;
                _lastHoveredBox = null;
                // No canvas invalidate: hover transitions no longer mutate
                // the text layout, so the link→null transition has no painted
                // representation either.
                _ = wasLink; _ = boxToInvalidate;
                if (wasLink)
                    InvalidateInteractiveTextAdorner();
            }
            SetCursorShape(null);
            return;
        }

        // Hover effect for links + cursor change.
        // Only respond to transitions that *actually affect rendering or the
        // cursor shape*.  Earlier this method invalidated the canvas and
        // re-set ProtectedCursor on every transition between any two runs
        // (including TextRun → TextRun within the same paragraph as the
        // pointer moved character-by-character).  That produced visible
        // text "shake" on plain hover because ProtectedCursor reassignment
        // and a full Canvas.Invalidate per pointer-move event nudges the
        // visual tree by a sub-pixel.  Now we only react when the link
        // we're hovering changes (which mutates run color → must repaint)
        // or when the cursor shape needs to flip Hand↔IBeam.
        InlineRun? hovered = null;
        Layout.Boxes.InlineContainerBox? hoveredBox = null;
        foreach (var b in _snapshot.Blocks)
        {
            if (FindInlineHover(b, pt) is var h && h.Run is not null)
            {
                hovered = h.Run;
                hoveredBox = h.Box;
                break;
            }
        }

        if (IsLinkedRun(hovered) && hoveredBox?.IsPointInsideRunBounds(hovered!, pt) != true)
        {
            hovered = null;
            hoveredBox = null;
        }

        var hoveredLinkedRun = IsLinkedRun(hovered) ? hovered : null;
        var lastLinkedRun = IsLinkedRun(_lastHoveredRun) ? _lastHoveredRun : null;
        bool linkChanged = !ReferenceEquals(hoveredLinkedRun, lastLinkedRun);
        bool overVectorScene = TryGetVectorPointerState(
            pt,
            out bool hoveredVectorLink,
            out bool hoveredVectorSelectable);
        var wantedShape = hoveredLinkedRun is not null || hoveredVectorLink
            ? Microsoft.UI.Input.InputSystemCursorShape.Hand
            : IsSelectionEnabled && (!overVectorScene || hoveredVectorSelectable)
                ? Microsoft.UI.Input.InputSystemCursorShape.IBeam
                : (Microsoft.UI.Input.InputSystemCursorShape?)null; // Arrow when selection is off

        if (linkChanged)
        {
            // Update hover bookkeeping for click routing, but do NOT invalidate
            // the canvas — hover causes no visual change to the text any more
            // (see InlineContainerBox.HoveredRun docs).  Eliminating these
            // partial-region invalidates is what finally killed the long-
            // standing text-shake bug: even with grayscale AA + device-pixel
            // origin snapping, repainting a partial canvas region forced
            // DirectWrite to re-tile glyphs at sub-pixel-different positions.
            foreach (var b in _snapshot.Blocks) ClearHover(b);
            if (hoveredBox is not null && hoveredLinkedRun is LinkRun hoveredLink)
                hoveredBox.HoveredRun = hoveredLink;
            InvalidateInteractiveTextAdorner();
        }
        _lastHoveredRun = hovered;
        _lastHoveredBox = hoveredBox;

        UpdateAbbreviationTooltip(hovered, hoveredBox, pt);
        SetCursorShape(wantedShape);
    }

    private void UpdateAbbreviationTooltip(
        InlineRun? hoveredRun,
        Layout.Boxes.InlineContainerBox? hoveredBox,
        Point pointerPoint)
    {
        var abbreviation = hoveredRun as AbbreviationRun;
        if (abbreviation is null ||
            string.IsNullOrWhiteSpace(abbreviation.Expansion) ||
            _canvas is null ||
            hoveredBox is null ||
            !hoveredBox.TryGetRunBounds(abbreviation, pointerPoint, out var placementRect))
        {
            ScheduleAbbreviationTooltipHide();
            return;
        }

        CancelAbbreviationTooltipHide();
        if (ReferenceEquals(abbreviation, _lastHoveredAbbreviation))
        {
            CancelAbbreviationTooltipShow();
            return;
        }

        if (ReferenceEquals(abbreviation, _pendingAbbreviationTooltipRun))
        {
            _pendingAbbreviationTooltipPlacementRect = placementRect;
            return;
        }

        if (_lastHoveredAbbreviation is not null)
            HideAbbreviationTooltip();

        ScheduleAbbreviationTooltipShow(abbreviation, placementRect);
    }

    private void ScheduleAbbreviationTooltipShow(AbbreviationRun abbreviation, Rect placementRect)
    {
        CancelAbbreviationTooltipHide();
        _pendingAbbreviationTooltipRun = abbreviation;
        _pendingAbbreviationTooltipPlacementRect = placementRect;

        var timer = EnsureAbbreviationTooltipShowTimer();
        if (timer is null)
        {
            ShowAbbreviationTooltip(abbreviation, placementRect);
            return;
        }

        timer.Stop();
        timer.Interval = AbbreviationTooltipShowDelay;
        timer.Start();
    }

    private void ShowPendingAbbreviationTooltip()
    {
        _abbreviationTooltipShowTimer?.Stop();
        var abbreviation = _pendingAbbreviationTooltipRun;
        var placementRect = _pendingAbbreviationTooltipPlacementRect;
        _pendingAbbreviationTooltipRun = null;

        if (_isUnloaded ||
            abbreviation is null ||
            !ReferenceEquals(abbreviation, _lastHoveredRun as AbbreviationRun))
        {
            return;
        }

        ShowAbbreviationTooltip(abbreviation, placementRect);
    }

    private void ShowAbbreviationTooltip(AbbreviationRun abbreviation, Rect placementRect)
    {
        if (_isUnloaded || _canvas is null || string.IsNullOrWhiteSpace(abbreviation.Expansion))
            return;

        CloseAbbreviationTooltip();
        _lastHoveredAbbreviation = abbreviation;
        _abbreviationToolTip = new ToolTip
        {
            Content = abbreviation.Expansion,
            IsHitTestVisible = false,
            Placement = PlacementMode.Top,
            PlacementTarget = _canvas,
            PlacementRect = placementRect,
            VerticalOffset = -4,
        };
        ToolTipService.SetToolTip(_canvas, _abbreviationToolTip);
        _abbreviationToolTip.IsOpen = true;
    }

    private void ScheduleAbbreviationTooltipHide()
    {
        CancelAbbreviationTooltipShow();
        _pendingAbbreviationTooltipRun = null;

        if (_abbreviationToolTip is null)
        {
            _lastHoveredAbbreviation = null;
            return;
        }

        var timer = EnsureAbbreviationTooltipHideTimer();
        if (timer is null)
        {
            HideAbbreviationTooltip();
            return;
        }

        timer.Stop();
        timer.Interval = AbbreviationTooltipHideDelay;
        timer.Start();
    }

    private void HideAbbreviationTooltip()
    {
        CancelAbbreviationTooltipShow();
        CancelAbbreviationTooltipHide();
        _pendingAbbreviationTooltipRun = null;
        CloseAbbreviationTooltip();
        _lastHoveredAbbreviation = null;
    }

    private void CloseAbbreviationTooltip()
    {
        if (_abbreviationToolTip is not null)
        {
            try { _abbreviationToolTip.IsOpen = false; } catch { }
            if (_canvas is not null)
                ToolTipService.SetToolTip(_canvas, null);
            _abbreviationToolTip = null;
        }
    }

    private void CancelAbbreviationTooltipShow()
    {
        _abbreviationTooltipShowTimer?.Stop();
    }

    private void CancelAbbreviationTooltipHide()
    {
        _abbreviationTooltipHideTimer?.Stop();
    }

    private void ReleaseAbbreviationTooltipTimers()
    {
        _abbreviationTooltipShowTimer?.Stop();
        _abbreviationTooltipHideTimer?.Stop();
        _abbreviationTooltipShowTimer = null;
        _abbreviationTooltipHideTimer = null;
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? EnsureAbbreviationTooltipShowTimer()
    {
        if (_abbreviationTooltipShowTimer is not null)
            return _abbreviationTooltipShowTimer;

        var dispatcher = DispatcherQueue ?? _canvas?.DispatcherQueue;
        if (dispatcher is null)
            return null;

        _abbreviationTooltipShowTimer = dispatcher.CreateTimer();
        _abbreviationTooltipShowTimer.Tick += (_, _) => ShowPendingAbbreviationTooltip();
        return _abbreviationTooltipShowTimer;
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? EnsureAbbreviationTooltipHideTimer()
    {
        if (_abbreviationTooltipHideTimer is not null)
            return _abbreviationTooltipHideTimer;

        var dispatcher = DispatcherQueue ?? _canvas?.DispatcherQueue;
        if (dispatcher is null)
            return null;

        _abbreviationTooltipHideTimer = dispatcher.CreateTimer();
        _abbreviationTooltipHideTimer.Tick += (_, _) => HideAbbreviationTooltip();
        return _abbreviationTooltipHideTimer;
    }

    private void InvalidateInteractiveTextAdorner()
    {
        if (_overlay is null)
            return;

        if (_selectionAdorner is null && !HasInteractiveTextAdornerContent())
            return;

        if (!EnsureSelectionAdorner())
            return;

        UpdateSelectionAdornerViewport();
        InvalidateSelectionAdorner();
    }

    private void RefreshInteractiveTextAdornerAfterLayoutChange()
    {
        // Lazy realization and image measurement can move text without replacing
        // the snapshot. Recompute selection geometry before redrawing the
        // viewport-bounded overlay; merely moving the surface would retain stale
        // highlight/text pixels at the old block positions.
        if (_selection.IsActive)
        {
            UpdateSelectionOverlay();
            return;
        }

        UpdateSelectionAdornerViewport();
        InvalidateSelectionAdorner();
    }

    private void InvalidateSelectionAdorner()
    {
        if (_isDisposed || _isUnloaded)
            return;

        if (!HasInteractiveTextAdornerContent())
        {
            if (_selectionAdorner is not null)
                _selectionAdorner.Visibility = Visibility.Collapsed;
            UpdateSelectionHandles();
            return;
        }

        if (!EnsureSelectionAdorner())
            return;

        UpdateSelectionAdornerViewport();
        if (_selectionAdornerSource is null)
            return;

        try
        {
            using var drawingSession = _selectionAdornerSource.CreateDrawingSession(Color.FromArgb(0, 0, 0, 0));
            drawingSession.TextAntialiasing = Microsoft.Graphics.Canvas.Text.CanvasTextAntialiasing.Grayscale;
            drawingSession.Transform = Matrix3x2.CreateTranslation(0, -(float)_selectionAdornerOffsetY);
            PaintInteractiveDocumentState(
                drawingSession,
                new Rect(
                    0,
                    _selectionAdornerOffsetY,
                    _selectionAdornerSourceWidth,
                    _selectionAdornerSourceHeight));

            if (ShakeLogger.IsEnabled)
            {
                ShakeLogger.Log(
                    "sel-adorner-draw",
                    $"rects={_selectionAdornerRects.Count} top={_selectionAdornerOffsetY:F2} " +
                    $"width={_selectionAdornerSourceWidth:F2} height={_selectionAdornerSourceHeight:F2}");
            }
        }
        catch (Exception ex) when (GraphicsDeviceErrors.IsDeviceLost(ex))
        {
            ReleaseSelectionAdornerSurface();
            HandleCanvasDeviceLost(ex);
        }
        catch (Exception ex) when (ShouldIgnoreShutdownException(ex))
        {
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] ignored selection invalidate during shutdown: {ex.GetType().Name} {GraphicsDeviceErrors.FormatHResult(ex.HResult)}");
        }
    }

    private void InvalidateCanvas()
    {
        if (_isDisposed || _isUnloaded || !IsLoaded)
            return;

        try
        {
            _canvas?.Invalidate();
        }
        catch (Exception ex) when (GraphicsDeviceErrors.IsDeviceLost(ex))
        {
            HandleCanvasDeviceLost(ex);
        }
        catch (Exception ex) when (ShouldIgnoreShutdownException(ex))
        {
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] ignored canvas invalidate during shutdown: {ex.GetType().Name} {GraphicsDeviceErrors.FormatHResult(ex.HResult)}");
        }
    }

    private static bool ShouldIgnoreShutdownException(Exception ex)
        => GraphicsDeviceErrors.IsShutdownOrDisposed(ex);

    private Point PrepareSelectionDragPoint(
        Point point,
        bool sustained = false,
        double elapsedSeconds = 0)
    {
        if (_canvas is null)
            return point;

        TryGetViewport(out double viewportTop, out double viewportHeight, out _);
        double pointerViewportY = point.Y - viewportTop;
        double delta = sustained
            ? SelectionAutoScroll.ComputeFrameDelta(
                point.Y,
                viewportTop,
                viewportHeight,
                elapsedSeconds)
            : SelectionAutoScroll.ComputeDirectionalDelta(
                point.Y,
                viewportTop,
                viewportHeight,
                _lastSelectionPointerViewportY);
        _lastSelectionPointerViewportY = pointerViewportY;
        if (Math.Abs(delta) > 0.0001)
        {
            double maxOffset = Math.Max(0, _canvas.ActualHeight - viewportHeight);
            double target = Math.Clamp(viewportTop + delta, 0, maxOffset);
            if (Math.Abs(target - viewportTop) > 0.0001)
            {
                if (_scroll is not null)
                    _scroll.ChangeView(null, target, null, disableAnimation: true);
                else
                    BringDocumentRectIntoExternalViewport(new Rect(0, target, 1, 1), animate: false);
                viewportTop = target;
            }
        }

        double x = Math.Clamp(point.X, 0, Math.Max(0, _canvas.ActualWidth - 1));
        double y = SelectionAutoScroll.ClampPointToViewport(point.Y, viewportTop, viewportHeight);
        return new Point(x, y);
    }

    /// <summary>
    /// Sets the cursor shape to <paramref name="shape"/> if it
    /// differs from the last-set shape, or resets it to <c>null</c> (system
    /// default — Arrow) when <paramref name="shape"/> is <c>null</c>.
    /// Resetting to null is essential when the pointer leaves text areas and
    /// enters overlay embeds: with null, XAML walks up the tree and finds no
    /// cursor override, so child elements (Button, CheckBox, …) can show their
    /// own cursors instead of inheriting IBeam from this UserControl.
    /// Cursors are cached as fields to avoid creating a new IDisposable on every
    /// Hand↔IBeam transition.
    /// </summary>
    private void SetCursorShape(Microsoft.UI.Input.InputSystemCursorShape? shape)
    {
        if (shape == _currentCursorShape) return;
        // Update tracked state first so repeated failures don't cause a per-frame
        // exception storm: if ProtectedCursor throws, we still record the intent
        // and skip the setter on the next move event.
        _currentCursorShape = shape;
        try
        {
            ProtectedCursor = shape switch
            {
                Microsoft.UI.Input.InputSystemCursorShape.Hand =>
                    _cursorHand ??= Microsoft.UI.Input.InputSystemCursor.Create(
                        Microsoft.UI.Input.InputSystemCursorShape.Hand),
                Microsoft.UI.Input.InputSystemCursorShape.IBeam =>
                    _cursorIBeam ??= Microsoft.UI.Input.InputSystemCursor.Create(
                        Microsoft.UI.Input.InputSystemCursorShape.IBeam),
                _ => null
            };
        }
        catch { /* ProtectedCursor isn't always settable */ }
    }

    private static (Layout.Boxes.InlineContainerBox? Box, InlineRun? Run) FindInlineHover(Layout.BlockBox box, Point pt)
    {
        switch (box)
        {
            case Layout.Boxes.InlineContainerBox icb:
                var r = icb.RunAt(pt);
                return r is not null ? (icb, r) : (null, null);
            case Layout.Boxes.CodeBlockBox codeBlock:
                foreach (var chunk in codeBlock.Chunks)
                {
                    var c = FindInlineHover(chunk, pt);
                    if (c.Run is not null) return c;
                }
                return (null, null);
            case Layout.Boxes.ListItemBox lib:
                var m = FindInlineHover(lib.Marker, pt);
                if (m.Run is not null) return m;
                return FindInlineHover(lib.Content, pt);
            case Layout.Boxes.TableBox tb:
                foreach (var cell in tb.GetCellBoxes())
                {
                    var c = FindInlineHover(cell, pt);
                    if (c.Run is not null) return c;
                }
                return (null, null);
            case Layout.Boxes.StackBox sb:
                foreach (var ch in sb.Children)
                {
                    var c = FindInlineHover(ch, pt);
                    if (c.Run is not null) return c;
                }
                return (null, null);
        }
        return (null, null);
    }

    private static void ClearHover(Layout.BlockBox box)
    {
        switch (box)
        {
            case Layout.Boxes.InlineContainerBox icb:
                icb.HoveredRun = null;
                break;
            case Layout.Boxes.CodeBlockBox codeBlock:
                foreach (var chunk in codeBlock.Chunks) ClearHover(chunk);
                break;
            case Layout.Boxes.ListItemBox lib:
                ClearHover(lib.Marker);
                ClearHover(lib.Content);
                break;
            case Layout.Boxes.TableBox tb:
                foreach (var cell in tb.GetCellBoxes()) ClearHover(cell);
                break;
            case Layout.Boxes.StackBox sb:
                foreach (var c in sb.Children) ClearHover(c);
                break;
        }
    }

    /// <summary>
    /// True when the given point in canvas coordinates is inside the
    /// rectangle of any realised inline or block embed.
    /// </summary>
    private bool IsPointOverEmbed(Point pt)
    {
        if (IsPointOverCodeBlockAction(pt))
            return true;

        for (int i = 0; i < _embedRects.Count; i++)
        {
            var r = _embedRects[i].Rect;
            if (pt.X >= r.X && pt.X < r.X + r.Width &&
                pt.Y >= r.Y && pt.Y < r.Y + r.Height)
                return true;
        }
        for (int i = 0; i < _blockEmbedRects.Count; i++)
        {
            var r = _blockEmbedRects[i].Rect;
            if (pt.X >= r.X && pt.X < r.X + r.Width &&
                pt.Y >= r.Y && pt.Y < r.Y + r.Height)
                return true;
        }
        return false;
    }

    private bool IsPointOverCodeBlockAction(Point pt)
    {
        for (int i = 0; i < _codeBlockActionRects.Count; i++)
        {
            var r = _codeBlockActionRects[i].Rect;
            if (pt.X >= r.X && pt.X < r.X + r.Width &&
                pt.Y >= r.Y && pt.Y < r.Y + r.Height)
                return true;
        }

        return false;
    }

    /// <summary>
    /// If the point is inside an inline embed's rectangle, returns a
    /// DocumentPosition that snaps to the start (left half) or end (right
    /// half) of the embed run — making the embed an atomic, indivisible
    /// unit of selection.  Returns false when the point is not over any
    /// embed.
    /// </summary>
    private bool TryHitTestEmbed(Point pt, out DocumentPosition position)
    {
        for (int i = 0; i < _embedRects.Count; i++)
        {
            var (box, run, r) = _embedRects[i];
            if (pt.X >= r.X && pt.X < r.X + r.Width &&
                pt.Y >= r.Y && pt.Y < r.Y + r.Height)
            {
                bool rightHalf = pt.X >= r.X + r.Width / 2.0;
                // In RTL flow the logical start of a run is visually on the
                // right, so the right half maps to charOffset 0 and the left
                // half maps to charOffset Text.Length. In LTR it's the
                // opposite.
                bool isRtl = FlowDirection == Microsoft.UI.Xaml.FlowDirection.RightToLeft;
                int charOffset = isRtl
                    ? (rightHalf ? 0 : run.Text.Length)
                    : (rightHalf ? run.Text.Length : 0);
                position = new DocumentPosition(box.BlockIndex, run.InlineIndex, charOffset);
                return true;
            }
        }

        for (int i = 0; i < _blockEmbedRects.Count; i++)
        {
            var (box, r) = _blockEmbedRects[i];
            if (pt.X >= r.X && pt.X < r.X + r.Width &&
                pt.Y >= r.Y && pt.Y < r.Y + r.Height)
            {
                bool afterMidpoint = pt.Y >= r.Y + r.Height / 2.0;
                position = new DocumentPosition(box.BlockIndex, 0, afterMidpoint ? 1 : 0);
                return true;
            }
        }

        position = default;
        return false;
    }

    /// <summary>
    /// Syncs the selection adorner geometry. The adorner is a single stable
    /// image-source child layered above the document text and below hosted controls.
    /// Selection changes only mutate this in-memory rect list and invalidate
    /// the adorner; they never invalidate the base document canvas or mutate
    /// the XAML child tree during a drag.
    /// </summary>
    private void UpdateSelectionOverlay()
    {
        if (_overlay is null) return;

        var snapshot = _snapshot;
        if (snapshot is null || !_selection.IsActive)
        {
            _selectionAdornerRects.Clear();
            InvalidateSelectionAdorner();
            UpdateSelectionHandles();
            return;
        }

        // Snap to *physical pixels*, not DIPs. At fractional DPI (125%, 150%)
        // a DIP edge lands at a fractional physical pixel. The adorner uses the
        // same snapped rectangles for background fill, clipping, diagnostics,
        // and foreground overpaint so all selection pixels move together.
        double scale = XamlRoot?.RasterizationScale ?? 1.0;
        if (scale <= 0) scale = 1.0;

        _selectionAdornerRects.Clear();
        foreach (var rect in _selection.GetHighlightRects(snapshot))
        {
            double pxX = Math.Floor(rect.X * scale);
            double pxY = Math.Floor(rect.Y * scale);
            double pxR = Math.Ceiling((rect.X + rect.Width) * scale);
            double pxB = Math.Ceiling((rect.Y + rect.Height) * scale);
            double x = pxX / scale;
            double y = pxY / scale;
            double w = (pxR - pxX) / scale;
            double h = (pxB - pxY) / scale;
            if (w <= 0 || h <= 0)
                continue;

            _selectionAdornerRects.Add(new Rect(x, y, w, h));

            // Diagnostic: log the *physical-pixel* coords for the first stripe
            // so we can verify they stay rock-stable across drag frames at
            // fractional DPI. If shake reappears these numbers will jitter.
            if (_selectionAdornerRects.Count == 1)
            {
                if (ShakeLogger.IsEnabled)
                    ShakeLogger.LogPaint(
                        "sel-rect-phys", -1, (float)(x * scale), (float)(y * scale),
                        (float)(w * scale), (float)(h * scale));
            }
        }

        if (!EnsureSelectionAdorner())
            return;

        UpdateSelectionAdornerViewport();
        InvalidateSelectionAdorner();
    }

    private bool HasInteractiveTextAdornerContent()
        => (_selection.IsActive && _selectionAdornerRects.Count > 0) ||
           (_lastHoveredRun is LinkRun && _lastHoveredBox is not null) ||
           TryGetFocusedLink(out _, out _);

    private Microsoft.UI.Xaml.Controls.Image CreateSelectionAdorner()
    {
        var adorner = new Microsoft.UI.Xaml.Controls.Image
        {
            IsHitTestVisible = false,
            UseLayoutRounding = true,
            Stretch = Stretch.Fill,
            // Keep the overlay inert until selection gives it viewport bounds.
            Width = 1,
            Height = 1,
            Visibility = Visibility.Collapsed,
        };
        Canvas.SetZIndex(adorner, 0);
        _selectionAdorner = adorner;
        return adorner;
    }

    private bool EnsureSelectionAdornerSurface(double width, double height)
    {
        if (_selectionAdorner is null ||
            !double.IsFinite(width) ||
            !double.IsFinite(height) ||
            width <= 0 ||
            height <= 0)
        {
            return false;
        }

        double rasterizationScale = XamlRoot?.RasterizationScale ?? 1.0;
        if (!double.IsFinite(rasterizationScale) || rasterizationScale <= 0)
            rasterizationScale = 1.0;
        float dpi = (float)(96.0 * rasterizationScale);

        bool sourceMatches = _selectionAdornerSource is not null &&
            Math.Abs(_selectionAdornerSourceWidth - width) <= 0.5 &&
            Math.Abs(_selectionAdornerSourceHeight - height) <= 0.5 &&
            Math.Abs(_selectionAdornerSourceDpi - dpi) <= 0.1f;
        if (sourceMatches)
            return true;

        CanvasImageSource? replacement = null;
        try
        {
            replacement = new CanvasImageSource(
                CanvasDevice.GetSharedDevice(),
                (float)Math.Ceiling(width),
                (float)Math.Ceiling(height),
                dpi);

            _selectionAdornerSource = replacement;
            _selectionAdornerSourceWidth = width;
            _selectionAdornerSourceHeight = height;
            _selectionAdornerSourceDpi = dpi;
            _selectionAdorner.Source = replacement;
            // CanvasImageSource is not IDisposable in the Win2D projection.
            // Detaching the old ImageSource and dropping the final managed
            // reference releases its compositor surface.
            return true;
        }
        catch (Exception ex) when (GraphicsDeviceErrors.IsDeviceLost(ex))
        {
            ReleaseSelectionAdornerSurface();
            HandleCanvasDeviceLost(ex);
            return false;
        }
        catch (Exception ex) when (ShouldIgnoreShutdownException(ex))
        {
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] ignored selection surface creation during shutdown: {ex.GetType().Name} {GraphicsDeviceErrors.FormatHResult(ex.HResult)}");
            return false;
        }
    }

    private void ReleaseSelectionAdornerSurface()
    {
        if (_selectionAdorner is not null)
            _selectionAdorner.Source = null;

        _selectionAdornerSource = null;
        _selectionAdornerSourceWidth = 0;
        _selectionAdornerSourceHeight = 0;
        _selectionAdornerSourceDpi = 0;
    }

    private bool EnsureSelectionAdorner()
    {
        if (_overlay is null)
            return false;

        if (_selectionAdorner is null)
            CreateSelectionAdorner();

        try
        {
            if (VisualTreeHelper.GetParent(_selectionAdorner) is null)
                _overlay.Children.Add(_selectionAdorner);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            MarkdownDiagnostics.WriteLine($"[MarkdownRendererControl] selection adorner attach failed: {ex.Message}");
            return false;
        }

        return true;
    }

    private void CreateSelectionHandles()
    {
        if (_overlay is null)
            return;

        _selectionStartHandle = CreateSelectionHandle(SelectionHandleEndpoint.Start);
        _selectionEndHandle = CreateSelectionHandle(SelectionHandleEndpoint.End);
        _overlay.Children.Add(_selectionStartHandle);
        _overlay.Children.Add(_selectionEndHandle);
    }

    private Button CreateSelectionHandle(SelectionHandleEndpoint endpoint)
    {
        var dot = new Ellipse
        {
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var handle = new Button
        {
            Tag = endpoint,
            Content = dot,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            // Handles own their drag. Without opting out of the inherited
            // ScrollViewer manipulation contract, DirectManipulation can cancel
            // the Button's pointer stream as soon as the contact starts moving.
            ManipulationMode = ManipulationModes.None,
            IsTabStop = true,
            UseSystemFocusVisuals = true,
            Visibility = Visibility.Collapsed,
        };

        string automationId = endpoint == SelectionHandleEndpoint.Start
            ? "MarkdownSelectionStartHandle"
            : "MarkdownSelectionEndHandle";
        AutomationProperties.SetAutomationId(handle, automationId);
        UpdateSelectionHandleLocalization(handle, endpoint);
        handle.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler(OnSelectionHandlePointerPressed),
            handledEventsToo: true);
        handle.AddHandler(
            UIElement.PointerMovedEvent,
            new PointerEventHandler(OnSelectionHandlePointerMoved),
            handledEventsToo: true);
        handle.AddHandler(
            UIElement.PointerReleasedEvent,
            new PointerEventHandler(OnSelectionHandlePointerReleased),
            handledEventsToo: true);
        handle.AddHandler(
            UIElement.PointerCanceledEvent,
            new PointerEventHandler(OnSelectionHandlePointerCanceled),
            handledEventsToo: true);
        handle.AddHandler(
            UIElement.PointerCaptureLostEvent,
            new PointerEventHandler(OnSelectionHandlePointerCanceled),
            handledEventsToo: true);
        handle.KeyDown += OnSelectionHandleKeyDown;
        Canvas.SetZIndex(handle, 5);
        return handle;
    }

    private bool EnsureSelectionHandles()
    {
        if (_overlay is null)
            return false;
        if (_selectionStartHandle is null || _selectionEndHandle is null)
            CreateSelectionHandles();

        bool attached = false;
        try
        {
            if (VisualTreeHelper.GetParent(_selectionStartHandle) is null)
            {
                _overlay.Children.Add(_selectionStartHandle);
                attached = true;
            }
            if (VisualTreeHelper.GetParent(_selectionEndHandle) is null)
            {
                _overlay.Children.Add(_selectionEndHandle);
                attached = true;
            }
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] selection handle attach failed: {ex.Message}");
            return false;
        }

        if (attached)
        {
            UpdateSelectionHandleLocalization(_selectionStartHandle!, SelectionHandleEndpoint.Start);
            UpdateSelectionHandleLocalization(_selectionEndHandle!, SelectionHandleEndpoint.End);
        }
        return true;
    }

    private void UpdateSelectionHandleLocalization(
        Button handle,
        SelectionHandleEndpoint endpoint)
    {
        string name = endpoint == SelectionHandleEndpoint.Start
            ? ResolveLocalizedString(
                MarkdownStringKeys.SelectionStartHandle,
                MarkdownLocalizedStrings.SelectionStartHandle)
            : ResolveLocalizedString(
                MarkdownStringKeys.SelectionEndHandle,
                MarkdownLocalizedStrings.SelectionEndHandle);
        string help = ResolveLocalizedString(
            MarkdownStringKeys.SelectionHandleHelp,
            MarkdownLocalizedStrings.SelectionHandleHelp);
        AutomationProperties.SetName(handle, name);
        AutomationProperties.SetHelpText(handle, help);
        ToolTipService.SetToolTip(handle, name);
    }

    private void UpdateSelectionHandles()
    {
        if (!_touchSelection.ShouldShowHandles ||
            !_selection.IsActive ||
            _selectionAdornerRects.Count == 0 ||
            !EnsureSelectionHandles())
        {
            SetSelectionHandleVisibility(Visibility.Collapsed);
            return;
        }

        double targetSize = Math.Clamp(
            _themeSnapshot?.MinimumInteractiveSize ?? 40,
            44,
            128);
        double dotSize = Math.Clamp(targetSize * 0.36, 12, 20);
        Color handleColor = _themeSnapshot?.SelectionHighlightColor
            ?? Color.FromArgb(0xFF, 0x00, 0x78, 0xD4);
        UpdateSelectionHandleAppearance(_selectionStartHandle!, targetSize, dotSize, handleColor);
        UpdateSelectionHandleAppearance(_selectionEndHandle!, targetSize, dotSize, handleColor);

        Point startPoint = GetSelectionHandleEndpointPoint(SelectionHandleEndpoint.Start);
        Point endPoint = GetSelectionHandleEndpointPoint(SelectionHandleEndpoint.End);
        PlaceSelectionHandle(_selectionStartHandle!, startPoint, targetSize);
        PlaceSelectionHandle(_selectionEndHandle!, endPoint, targetSize);
    }

    private static void UpdateSelectionHandleAppearance(
        Button handle,
        double targetSize,
        double dotSize,
        Color color)
    {
        handle.Width = targetSize;
        handle.Height = targetSize;
        handle.MinWidth = targetSize;
        handle.MinHeight = targetSize;
        handle.CornerRadius = new CornerRadius(targetSize / 2.0);
        if (handle.Content is not Ellipse dot)
            return;

        dot.Width = dotSize;
        dot.Height = dotSize;
        if (dot.Fill is SolidColorBrush brush)
            brush.Color = color;
        else
            dot.Fill = new SolidColorBrush(color);
    }

    private void PlaceSelectionHandle(Button handle, Point endpoint, double targetSize)
    {
        TryGetViewport(out double viewportTop, out double viewportHeight, out double viewportWidth);
        double viewportBottom = viewportTop + viewportHeight;
        bool visible = endpoint.Y + targetSize / 2.0 >= viewportTop &&
                       endpoint.Y - targetSize / 2.0 <= viewportBottom;
        if (!visible)
        {
            handle.Visibility = Visibility.Collapsed;
            return;
        }

        double maxLeft = Math.Max(0, viewportWidth - targetSize);
        Canvas.SetLeft(handle, Math.Clamp(endpoint.X - targetSize / 2.0, 0, maxLeft));
        Canvas.SetTop(handle, Math.Max(0, endpoint.Y - targetSize * 0.22));
        handle.Visibility = Visibility.Visible;
    }

    private void SetSelectionHandleVisibility(Visibility visibility)
    {
        if (_selectionStartHandle is not null)
            _selectionStartHandle.Visibility = visibility;
        if (_selectionEndHandle is not null)
            _selectionEndHandle.Visibility = visibility;
    }

    internal void AppendVisibleSelectionHandleAutomationPeers(IList<AutomationPeer> peers)
    {
        AppendVisibleSelectionHandleAutomationPeer(_selectionStartHandle, peers);
        AppendVisibleSelectionHandleAutomationPeer(_selectionEndHandle, peers);
    }

    private static void AppendVisibleSelectionHandleAutomationPeer(
        Button? handle,
        IList<AutomationPeer> peers)
    {
        if (handle is null || handle.Visibility != Visibility.Visible)
            return;

        AutomationPeer? peer = FrameworkElementAutomationPeer.FromElement(handle) ??
            FrameworkElementAutomationPeer.CreatePeerForElement(handle);
        if (peer is not null)
            peers.Add(peer);
    }

    private void ProcessSelectionHandlePointerPressed(ref PointerInput e)
    {
        if (e.Sender is not Button { Tag: SelectionHandleEndpoint endpoint } handle ||
            endpoint == SelectionHandleEndpoint.None ||
            _snapshot is null || !_selection.IsActive || _canvas is null)
        {
            return;
        }

        var point = e.Point;
        MarkdownPointerModality modality = GetPointerModality(e.Pointer.PointerDeviceType);
        bool primary = modality == MarkdownPointerModality.Touch
            ? point.IsInContact
            : MarkdownPointerGesturePolicy.IsPrimarySelectionPress(
                modality,
                point.IsInContact,
                point.Properties.IsLeftButtonPressed,
                point.Properties.IsBarrelButtonPressed);
        if (!primary)
            return;

        DocumentRange range = _selection.Range.Normalized();
        _activeSelectionHandle = endpoint;
        _selectionHandlePointerId = e.Pointer.PointerId;
        _selectionHandleFixedPosition = endpoint == SelectionHandleEndpoint.Start
            ? range.End
            : range.Start;
        TryGetViewport(out double viewportTop, out _, out _);
        _lastSelectionPointerViewportY = point.Position.Y - viewportTop;
        Point endpointPoint = GetSelectionHandleEndpointPoint(endpoint);
        _selectionHandleCaretOffset = new Point(
            endpointPoint.X - point.Position.X,
            endpointPoint.Y - point.Position.Y);
        _selectionHandleLatestPoint = point.Position;
        _selectionHandleLatestViewportY = point.Position.Y - viewportTop;
        _selectionHandleMovePending = false;
        if (!_touchSelection.BeginHandleDrag(
                e.Pointer.PointerId,
                endpoint == SelectionHandleEndpoint.Start))
        {
            ResetSelectionHandleDrag();
            return;
        }
        if (!CapturePointerForInput(handle, e))
        {
            ResetSelectionHandleDrag();
            return;
        }

        SetSelectionHandleGlyphVisibility(visible: false);
        StartSelectionHandleFrameClock();
        e.Handled = true;
    }

    private void ProcessSelectionHandlePointerMoved(ref PointerInput e)
    {
        if (_activeSelectionHandle == SelectionHandleEndpoint.None ||
            _selectionHandlePointerId != e.Pointer.PointerId ||
            _snapshot is null || _canvas is null)
        {
            return;
        }

        var pointerPoint = e.Point;
        if (!pointerPoint.IsInContact)
            return;

        _selectionHandleLatestPoint = pointerPoint.Position;
        TryGetViewport(out double viewportTop, out _, out _);
        _selectionHandleLatestViewportY = pointerPoint.Position.Y - viewportTop;
        _selectionHandleMovePending = true;
        StartSelectionHandleFrameClock();
        e.Handled = true;
    }

    private void ProcessSelectionHandlePointerReleased(ref PointerInput e)
    {
        if (_selectionHandlePointerId != e.Pointer.PointerId)
            return;

        // Commit the release coordinate even when the dispatcher coalesced the
        // final PointerMoved event. This mirrors the main text-drag path and
        // prevents quick flick-and-release gestures from stopping one frame early.
        _selectionHandleLatestPoint = e.Point.Position;
        TryGetViewport(out double viewportTop, out _, out _);
        _selectionHandleLatestViewportY = e.Point.Position.Y - viewportTop;
        _selectionHandleMovePending = true;
        ApplyPendingSelectionHandleMove(sustained: false);
        if (_canvas is not null && e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch)
            SuppressTouchTap(e.Point.Position);
        _touchSelection.CompleteHandleDrag(e.Pointer.PointerId);
        _activeSelectionHandle = SelectionHandleEndpoint.None;
        _selectionHandlePointerId = 0;
        _lastSelectionPointerViewportY = double.NaN;
        StopSelectionHandleFrameClock();
        SetSelectionHandleGlyphVisibility(visible: true);
        if (e.Sender is UIElement element)
            element.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void ProcessSelectionHandlePointerCanceled(ref PointerInput e)
    {
        if (_selectionHandlePointerId != e.Pointer.PointerId)
            return;

        _touchSelection.Cancel(e.Pointer.PointerId);
        ResetSelectionHandleDrag();
        UpdateSelectionHandles();
        e.Handled = true;
    }

    private void StartSelectionHandleFrameClock()
    {
        if (_selectionHandleFrameSubscribed)
            return;

        _selectionHandleLastFrameTimestamp = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += OnSelectionHandleFrame;
        _selectionHandleFrameSubscribed = true;
    }

    private void OnSelectionHandleFrame(object? sender, object args)
    {
        if (_isDisposed || _isUnloaded ||
            _activeSelectionHandle == SelectionHandleEndpoint.None ||
            _snapshot is not { } snapshot)
        {
            StopSelectionHandleFrameClock();
            return;
        }

        if (!snapshot.TryBeginInteraction())
            return;

        try
        {
            long timestamp = Stopwatch.GetTimestamp();
            double elapsedSeconds = Math.Max(
                0,
                (timestamp - _selectionHandleLastFrameTimestamp) / (double)Stopwatch.Frequency);
            _selectionHandleLastFrameTimestamp = timestamp;

            // Rendering is the coalescing boundary: at most one hit-test and
            // overlay update is performed per compositor frame. The latest
            // contact remains active without PointerMoved so edge scrolling
            // continues while the finger is stationary.
            bool keepRunning = ApplyPendingSelectionHandleMove(
                sustained: true,
                elapsedSeconds);
            if (!keepRunning)
                StopSelectionHandleFrameClock();
        }
        finally
        {
            snapshot.EndInteraction();
        }
    }

    private bool ApplyPendingSelectionHandleMove(
        bool sustained,
        double elapsedSeconds = 0)
    {
        if (_activeSelectionHandle == SelectionHandleEndpoint.None ||
            _snapshot is null || _canvas is null ||
            (!sustained && !_selectionHandleMovePending))
        {
            return false;
        }

        TryGetViewport(out double viewportTop, out _, out _);
        Point contact = new(
            _selectionHandleLatestPoint.X,
            viewportTop + _selectionHandleLatestViewportY);
        Point caretPoint = new(
            contact.X + _selectionHandleCaretOffset.X,
            contact.Y + _selectionHandleCaretOffset.Y);
        if (sustained && !_selectionHandleMovePending)
        {
            TryGetViewport(out double edgeViewportTop, out double edgeViewportHeight, out _);
            if (Math.Abs(SelectionAutoScroll.ComputeVelocity(
                    caretPoint.Y,
                    edgeViewportTop,
                    edgeViewportHeight)) < 0.5)
            {
                return false;
            }
        }
        Point point = PrepareSelectionDragPoint(caretPoint, sustained, elapsedSeconds);
        _selectionHandleMovePending = false;

        DocumentPosition candidate;
        if (TryHitTestEmbed(point, out DocumentPosition embedPosition))
            candidate = embedPosition;
        else if (!_snapshot.HitTestSelectionEndpoint(point, out candidate))
            return IsSelectionHandleInAutoScrollBand(caretPoint);

        AdjustSelectionHandle(_activeSelectionHandle, candidate);
        return IsSelectionHandleInAutoScrollBand(caretPoint);
    }

    private bool IsSelectionHandleInAutoScrollBand(Point caretPoint)
    {
        if (_canvas is null)
            return false;

        TryGetViewport(out double viewportTop, out double viewportHeight, out _);
        double velocity = SelectionAutoScroll.ComputeVelocity(
            caretPoint.Y,
            viewportTop,
            viewportHeight);
        if (Math.Abs(velocity) < 0.5)
            return false;

        double maxOffset = Math.Max(0, _canvas.ActualHeight - viewportHeight);
        return velocity < 0
            ? viewportTop > 0.0001
            : viewportTop < maxOffset - 0.0001;
    }

    private Point GetSelectionHandleEndpointPoint(SelectionHandleEndpoint endpoint)
    {
        if (_selectionAdornerRects.Count == 0)
            return default;

        if (_snapshot is not null && _selection.IsActive)
        {
            DocumentRange normalized = _selection.Range.Normalized();
            DocumentPosition position = endpoint == SelectionHandleEndpoint.Start
                ? normalized.Start
                : normalized.End;
            Layout.Boxes.InlineContainerBox? inline = FindInlineContainerAt(
                _snapshot,
                position.BlockIndex);
            if (inline is not null && inline.TryGetCaretPoint(
                    position,
                    rangeStart: endpoint == SelectionHandleEndpoint.Start,
                    out Point caretPoint))
            {
                return SnapSelectionHandlePoint(caretPoint);
            }
        }

        Rect rectangle = endpoint == SelectionHandleEndpoint.Start
            ? _selectionAdornerRects[0]
            : _selectionAdornerRects[^1];
        bool rightToLeft = FlowDirection == FlowDirection.RightToLeft;
        return endpoint == SelectionHandleEndpoint.Start
            ? new Point(rightToLeft ? rectangle.Right : rectangle.Left, rectangle.Bottom)
            : new Point(rightToLeft ? rectangle.Left : rectangle.Right, rectangle.Bottom);
    }

    private Point SnapSelectionHandlePoint(Point point)
    {
        double scale = XamlRoot?.RasterizationScale ?? 1.0;
        if (!double.IsFinite(scale) || scale <= 0)
            scale = 1.0;

        return new Point(
            Math.Round(point.X * scale) / scale,
            Math.Round(point.Y * scale) / scale);
    }

    private void SetSelectionHandleGlyphVisibility(bool visible)
    {
        double opacity = visible ? 1 : 0;
        if (_selectionStartHandle?.Content is Ellipse startGlyph)
            startGlyph.Opacity = opacity;
        if (_selectionEndHandle?.Content is Ellipse endGlyph)
            endGlyph.Opacity = opacity;
    }

    private void StopSelectionHandleFrameClock()
    {
        if (!_selectionHandleFrameSubscribed)
            return;

        CompositionTarget.Rendering -= OnSelectionHandleFrame;
        _selectionHandleFrameSubscribed = false;
        _selectionHandleLastFrameTimestamp = 0;
    }

    private void OnSelectionHandleKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not Button { Tag: SelectionHandleEndpoint endpoint } ||
            !_selection.IsActive)
        {
            return;
        }

        if (e.Key == VirtualKey.Escape)
        {
            FocusRendererForPointerInteraction();
            e.Handled = true;
            return;
        }

        int delta = e.Key switch
        {
            VirtualKey.Left => FlowDirection == FlowDirection.RightToLeft ? 1 : -1,
            VirtualKey.Right => FlowDirection == FlowDirection.RightToLeft ? -1 : 1,
            VirtualKey.Up => -1,
            VirtualKey.Down => 1,
            _ => 0,
        };
        if (delta == 0)
            return;

        MoveSelectionHandleByTextOffset(endpoint, delta);
        e.Handled = true;
    }

    private void MoveSelectionHandleByTextOffset(
        SelectionHandleEndpoint endpoint,
        int delta)
    {
        if (_snapshot is null || !_selection.IsActive)
            return;

        DocumentRange range = _selection.Range.Normalized();
        DocumentPosition current = endpoint == SelectionHandleEndpoint.Start
            ? range.Start
            : range.End;
        int currentOffset = _snapshot.SemanticDocument.TextOffsetFromDocumentPosition(current);
        int requestedOffset = _snapshot.SemanticDocument.TextElementBoundaries.Move(
            currentOffset,
            delta,
            out _);
        AdjustSelectionHandleAtTextOffset(
            endpoint,
            requestedOffset,
            candidateIsBoundary: true);
    }

    private void AdjustSelectionHandle(
        SelectionHandleEndpoint endpoint,
        DocumentPosition candidate)
    {
        if (_snapshot is null || !_selection.IsActive)
            return;

        var semantic = _snapshot.SemanticDocument;
        int rawCandidateOffset = semantic.TextOffsetFromDocumentPosition(candidate);
        AdjustSelectionHandleAtTextOffset(
            endpoint,
            rawCandidateOffset,
            candidateIsBoundary: false);
    }

    private void AdjustSelectionHandleAtTextOffset(
        SelectionHandleEndpoint endpoint,
        int rawCandidateOffset,
        bool candidateIsBoundary)
    {
        if (_snapshot is null || !_selection.IsActive)
            return;

        var semantic = _snapshot.SemanticDocument;
        int fixedOffset = semantic.TextOffsetFromDocumentPosition(_selectionHandlePointerId != 0
            ? _selectionHandleFixedPosition
            : endpoint == SelectionHandleEndpoint.Start
                ? _selection.Range.Normalized().End
                : _selection.Range.Normalized().Start);
        bool pointerDrag = _selectionHandlePointerId != 0;
        TouchSelectionEndpointAdjustment adjustment = TouchSelectionEndpointPolicy.Resolve(
            semantic.TextElementBoundaries,
            fixedOffset,
            rawCandidateOffset,
            endpoint == SelectionHandleEndpoint.Start,
            pointerDrag,
            candidateIsBoundary);
        int candidateOffset = adjustment.Offset;
        if (candidateOffset == fixedOffset)
            return;
        DocumentPosition? adjusted = semantic.PositionFromTextOffset(candidateOffset);
        if (adjusted is null)
            return;

        DocumentPosition fixedPosition = semantic.PositionFromTextOffset(fixedOffset)
            ?? _selectionHandleFixedPosition;
        _selection.SetAnchor(fixedPosition);
        _selection.ExtendTo(adjusted.Value);
        if (pointerDrag)
            _activeSelectionHandle = adjustment.IsStartEndpoint
                ? SelectionHandleEndpoint.Start
                : SelectionHandleEndpoint.End;
        _touchSelection.Select();
    }

    private void ResetSelectionHandleDrag()
    {
        _activeSelectionHandle = SelectionHandleEndpoint.None;
        _selectionHandlePointerId = 0;
        _selectionHandleFixedPosition = default;
        _selectionHandleLatestPoint = default;
        _selectionHandleLatestViewportY = double.NaN;
        _selectionHandleCaretOffset = default;
        _selectionHandleMovePending = false;
        _lastSelectionPointerViewportY = double.NaN;
        StopSelectionHandleFrameClock();
        SetSelectionHandleGlyphVisibility(visible: true);
        try { _selectionStartHandle?.ReleasePointerCaptures(); } catch { }
        try { _selectionEndHandle?.ReleasePointerCaptures(); } catch { }
    }

    private void DetachSelectionHandleHandlers()
    {
        DetachSelectionHandleHandlers(_selectionStartHandle);
        DetachSelectionHandleHandlers(_selectionEndHandle);
    }

    private void DetachSelectionHandleHandlers(Button? handle)
    {
        if (handle is null)
            return;
        handle.RemoveHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler(OnSelectionHandlePointerPressed));
        handle.RemoveHandler(
            UIElement.PointerMovedEvent,
            new PointerEventHandler(OnSelectionHandlePointerMoved));
        handle.RemoveHandler(
            UIElement.PointerReleasedEvent,
            new PointerEventHandler(OnSelectionHandlePointerReleased));
        handle.RemoveHandler(
            UIElement.PointerCanceledEvent,
            new PointerEventHandler(OnSelectionHandlePointerCanceled));
        handle.RemoveHandler(
            UIElement.PointerCaptureLostEvent,
            new PointerEventHandler(OnSelectionHandlePointerCanceled));
        handle.KeyDown -= OnSelectionHandleKeyDown;
    }

    private void SetSelectionDragShieldActive(bool active)
    {
        if (_overlay is null)
            return;

        if (!active)
        {
            if (_selectionDragShield is not null)
                _selectionDragShield.Visibility = Visibility.Collapsed;
            return;
        }

        if (!EnsureSelectionDragShield())
            return;

        _selectionDragShield!.Width = GetSelectionOverlayWidth();
        _selectionDragShield.Height = GetSelectionOverlayHeight();
        _selectionDragShield.Visibility = Visibility.Visible;
    }

    private Border CreateSelectionDragShield()
    {
        var shield = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            IsHitTestVisible = true,
            Visibility = Visibility.Collapsed,
        };
        shield.PointerMoved += OnPointerMoved;
        shield.PointerReleased += OnPointerReleased;
        shield.PointerCanceled += OnPointerCanceledOrCaptureLost;
        shield.PointerCaptureLost += OnPointerCanceledOrCaptureLost;
        Canvas.SetLeft(shield, 0);
        Canvas.SetTop(shield, 0);
        Canvas.SetZIndex(shield, 4);
        _selectionDragShield = shield;
        return shield;
    }

    private bool EnsureSelectionDragShield()
    {
        if (_overlay is null)
            return false;

        if (_selectionDragShield is null)
            CreateSelectionDragShield();

        try
        {
            if (VisualTreeHelper.GetParent(_selectionDragShield) is null)
                _overlay.Children.Add(_selectionDragShield);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            MarkdownDiagnostics.WriteLine($"[MarkdownRendererControl] selection drag shield attach failed: {ex.Message}");
            return false;
        }

        return true;
    }

    private double GetSelectionOverlayWidth()
    {
        TryGetViewport(out _, out _, out double width);
        return double.IsFinite(width) && width > 0 ? width : 1.0;
    }

    private double GetSelectionOverlayHeight()
    {
        TryGetViewport(out _, out double height, out _);
        return double.IsFinite(height) && height > 0 ? height : 1.0;
    }

    private void UpdateSelectionAdornerViewport()
    {
        if (_selectionAdorner is null)
            return;

        bool hasContent = HasInteractiveTextAdornerContent();
        if (!hasContent)
        {
            _selectionAdorner.Visibility = Visibility.Collapsed;
            UpdateSelectionHandles();
            return;
        }

        double width = GetSelectionOverlayWidth();
        double height = GetSelectionOverlayHeight();
        TryGetViewport(out double top, out _, out _);

        if (double.IsNaN(_selectionAdorner.Width) || Math.Abs(_selectionAdorner.Width - width) > 0.5)
            _selectionAdorner.Width = width;
        if (double.IsNaN(_selectionAdorner.Height) || Math.Abs(_selectionAdorner.Height - height) > 0.5)
            _selectionAdorner.Height = height;

        if (!EnsureSelectionAdornerSurface(width, height))
        {
            _selectionAdorner.Visibility = Visibility.Collapsed;
            UpdateSelectionHandles();
            return;
        }

        double currentLeft = Canvas.GetLeft(_selectionAdorner);
        if (double.IsNaN(currentLeft) || Math.Abs(currentLeft) > 0.1)
            Canvas.SetLeft(_selectionAdorner, 0);

        double currentTop = Canvas.GetTop(_selectionAdorner);
        if (double.IsNaN(currentTop) || Math.Abs(currentTop - top) > 0.1)
            Canvas.SetTop(_selectionAdorner, top);

        _selectionAdornerOffsetY = top;
        _selectionAdorner.Visibility = Visibility.Visible;
        UpdateSelectionHandles();
    }

    private void PaintInteractiveDocumentState(CanvasDrawingSession drawingSession, Rect viewport)
    {
        LayoutSnapshot? snapshot = _snapshot;
        if (snapshot is null)
            return;

        PaintLinkStateOverlay(drawingSession, viewport);
        if (!_selection.IsActive || _selectionAdornerRects.Count == 0)
            return;

        Color selectedBackground = _themeSnapshot?.SelectionHighlightColor
            ?? Color.FromArgb(0xFF, 0x66, 0xAA, 0xE8);
        Color selectedForeground = _themeSnapshot?.SelectionForegroundColor
            ?? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        foreach (Rect rectangle in _selectionAdornerRects)
        {
            if (rectangle.Right < viewport.Left || rectangle.Left > viewport.Right ||
                rectangle.Bottom < viewport.Top || rectangle.Top > viewport.Bottom)
            {
                continue;
            }

            drawingSession.FillRectangle(rectangle, selectedBackground);
        }
        snapshot.PaintSelectionForeground(
            drawingSession,
            _selection.Range.Normalized(),
            selectedForeground,
            viewport);
    }

    private void PaintLinkStateOverlay(CanvasDrawingSession drawingSession, Rect viewport)
    {
        LinkRun? hoveredLink = null;
        if (_lastHoveredRun is LinkRun hover && _lastHoveredBox is { } hoverBox)
        {
            hoveredLink = hover;
            hoverBox.PaintLinkStateForeground(drawingSession, hover, focused: false, viewport);
        }

        if (TryGetFocusedLink(out var focusedBox, out var focusedLink) &&
            !ReferenceEquals(focusedLink, hoveredLink))
        {
            focusedBox.PaintLinkStateForeground(drawingSession, focusedLink, focused: true, viewport);
        }
        else if (TryGetFocusedLink(out focusedBox, out focusedLink))
        {
            focusedBox.PaintLinkStateForeground(drawingSession, focusedLink, focused: true, viewport);
        }
    }

    private void ProcessPointerCanceledOrCaptureLost(ref PointerInput e)
    {
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch)
        {
            bool hadTouchSelection = _touchSelection.ShouldShowHandles && _selection.IsActive;
            bool canceledOwner = _touchSelection.Cancel(e.Pointer.PointerId);
            if (canceledOwner && hadTouchSelection && _touchSelection.State == TouchSelectionState.Canceled)
                ClearSelectionForExternalInteraction();
        }

        if (HandleHorizontalOverflowPointerCanceled(ref e))
            return;

        if (!IsActivePointerSessionEvent(e))
            return;

        e.Handled = true;

        // True interruption (system-level cancel or capture taken by another element):
        // tear down drag state so a phantom anchor doesn't survive into the next gesture.
        // We deliberately do NOT do this on plain PointerExited — with capture, the
        // pointer can briefly leave canvas bounds during a normal drag, and clearing
        // the anchor there would kill drag-select on the very first vertical move
        // when the pointer crosses into a sibling overlay region (e.g. over a hosted
        // inline embed). PointerReleased handles the normal end-of-drag cleanup.
        _pointerSession = default;
        _selectionAnchor = null;
        _lastSelectionPointerViewportY = double.NaN;
        _clickMode = ClickMode.Single;
        SetSelectionDragShieldActive(false);
        if (!_selection.Range.Normalized().IsEmpty)
            InvalidateSelectionAdorner();
        ProcessPointerExited(ref e);
        ResumePendingRebuildAfterPointerInteraction();
    }

    private void ProcessPointerExited(ref PointerInput e)
    {
        ScheduleAbbreviationTooltipHide();

        // PointerExited fires when the pointer leaves canvas bounds. During an
        // active captured drag this is expected (drag through hosted embeds /
        // adjacent areas) so we MUST NOT clear _selectionAnchor here — that
        // killed drag-select on the embeds page. Capture-loss / cancel are
        // routed to OnPointerCanceledOrCaptureLost which does the real cleanup.

        // ROOT-CAUSE FIX for text-selection shake:
        // During an active drag (_selectionAnchor != null), PointerExited fires
        // whenever the captured pointer physically leaves the canvas bounds (e.g.
        // the user drags toward the window edge). Calling InvalidateLinkHoverRegion
        // here issues a partial _canvas.Invalidate(), which triggers a tile repaint
        // that calls InlineContainerBox.Paint() → ApplyHoverColor() → CanvasTextLayout
        // .SetColor() → DirectWrite invalidates cached glyph-run metrics → character
        // region coordinates shift by sub-pixel amouits → visible text shake on every
        // drag frame. The canvas was already fully invalidated at drag-start
        // (OnPointerPressed) so no intermediate repaint is needed during the drag.
        // OnPointerCanceledOrCaptureLost clears _selectionAnchor *before* calling us,
        // so real cleanup (cancel / capture-loss) still runs normally here.
        if (_selectionAnchor is not null)
        {
            // Cursor reset is still safe and needed: if the pointer crosses from
            // the canvas into a hosted embed during drag, the embed must see the
            // default cursor (not an inherited IBeam).
            SetCursorShape(null);
            return;
        }

        // Clear hover state when the pointer leaves the canvas (or capture is
        // lost).  Without this, a link's hover colour and the hand cursor
        // persist even when the pointer is no longer over the control.
        if (_snapshot is null || _canvas is null) return;
        bool hadHover = _lastHoveredRun is not null;
        if (hadHover)
        {
            // Only LinkRun hovers produce a visible color change. Plain TextRun
            // hover state is purely cursor/IBeam tracking and has no painted
            // representation. Calling _canvas.Invalidate for a TextRun→null
            // transition causes a partial-tile repaint, and CanvasVirtualControl
            // partial repaiits expose DirectWrite sub-pixel glyph-position
            // variance at tile boundaries — i.e. the text-shake the user sees
            // on click-to-dismiss (capture-loss routes through here with
            // _selectionAnchor already nulled by OnPointerCanceledOrCaptureLost).
            bool wasLink = _lastHoveredRun is LinkRun;
            var boxToInvalidate = _lastHoveredBox;
            foreach (var b in _snapshot.Blocks) ClearHover(b);
            _lastHoveredRun = null;
            _lastHoveredBox = null;
            // No canvas invalidate: hover transitions no longer mutate the
            // text layout (see InlineContainerBox.HoveredRun docs).
            _ = wasLink; _ = boxToInvalidate;
            if (wasLink)
                InvalidateInteractiveTextAdorner();
        }
        // Always reset cursor to null (system default) on exit — not just
        // when a link was hovered.  PointerExited fires whenever the pointer
        // moves from the canvas to any sibling element — most importantly the
        // overlay that hosts embedded WinUI elements.  With ProtectedCursor =
        // null on this UserControl, XAML finds no cursor override anywhere in
        // the tree above the embed, so the embed (Button, CheckBox, …) can
        // show its own cursor instead of inheriting IBeam from us.
        SetCursorShape(null);
    }

    private void ProcessPointerReleased(ref PointerInput e)
    {
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch)
            _touchSelection.EndContact(e.Pointer.PointerId);

        if (_canvas is null) return;
        if (HandleHorizontalOverflowPointerReleased(ref e))
            return;
        if (!IsActivePointerSessionEvent(e))
            return;

        e.Handled = true;

        // PointerMoved is coalescible, including while the UI dispatcher is
        // busy. Commit the release location before ending capture so a fast
        // press/drag/release cannot leave selection at the last delivered move.
        if (_selectionAnchor is not null)
            ProcessPointerMoved(ref e);

        // Snapshot BEFORE releasing capture: ReleasePointerCapture can dispatch
        // PointerCaptureLost synchronously, so read and clear the pointer session first.
        bool wasLeft = _pointerSession.IsActive &&
            _pointerSession.PointerId == e.Pointer.PointerId &&
            _pointerSession.IsPrimary;
        _pointerSession = default;
        _selectionAnchor = null;
        _lastSelectionPointerViewportY = double.NaN;
        SetSelectionDragShieldActive(false);
        _canvas.ReleasePointerCapture(e.Pointer);
        ResumePendingRebuildAfterPointerInteraction();
        if (!wasLeft) return;

        if (!_selection.Range.Normalized().IsEmpty)
        {
            UpdateSelectionAdornerViewport();
            InvalidateSelectionAdorner();
        }

        // Click handling for links: if no real selection occurred, raise LinkClick
        // when the click lands on a LinkRun.
        if (_snapshot is null) return;
        if (_clickMode != ClickMode.Single) return; // double/triple-click: selection intent, not link-click
        if (!_selection.Range.Normalized().IsEmpty) return; // text was dragged — not a click
        var pt = e.Point.Position;
        ActivateLinkAtPoint(pt, GetLinkInputKind(e.Pointer.PointerDeviceType));
    }

    private bool ActivateLinkAtPoint(Point point, MarkdownLinkInputKind inputKind)
    {
        if (_snapshot is null)
            return false;

        LinkTarget? link = FindVectorLinkTargetAtPoint(point);
        if (link is null && _snapshot.HitTest(point, out var position))
            link = FindLinkTargetAt(position, point);
        if (link is null)
            return false;

        return ActivateLinkTarget(link.Value, inputKind, GetCurrentInputModifiers());
    }

    private bool ActivateLinkTarget(
        LinkTarget link,
        MarkdownLinkInputKind inputKind,
        MarkdownInputModifiers modifiers)
    {
        if (TryActivateDisclosure(link.Run))
            return true;

        // Intercept internal fragment anchors (e.g. footnote back/forward
        // links) and scroll without surfacing them to external subscribers.
        bool internalTargetHandled =
            link.Url.StartsWith("#", StringComparison.Ordinal) &&
            HandleInternalAnchor(link.Url);
        MarkdownLinkActivationDecision decision;
        try
        {
            decision = MarkdownLinkActivationPolicy.Evaluate(
                inputKind,
                internalTargetHandled,
                CommandProvider,
                link.SourceSpan,
                link.Url,
                link.Action);
        }
        catch (Exception ex)
        {
            // Provider failures must not make built-in link activation vanish.
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] Host activation command failed: {ex.Message}");
            decision = MarkdownLinkActivationPolicy.CreateHostFallback(inputKind);
        }

        if (decision.IsHandled)
            return true;

        LinkClick?.Invoke(this, new MarkdownLinkClickEventArgs(
            link.Url,
            link.Title,
            link.SourceSpan,
            decision.InputKind,
            modifiers,
            link.External
                ? MarkdownLinkDisposition.External
                : GetRequestedDisposition(modifiers),
            link.Action));
        return true;
    }

    private void ResumePendingRebuildAfterPointerInteraction()
    {
        if (_hasPendingRebuild && !_rebuildDispatchState.IsQueued && !_isUnloaded)
            RequestRebuild(_pendingRebuildReason);
    }

    /// <summary>
    /// Scrolls the document so the block with <paramref name="blockIndex"/>
    /// is near the top of the viewport.
    /// </summary>
    public void ScrollToBlock(int blockIndex)
    {
        if (_snapshot is not { } snapshot) return;

        BlockBox? targetTopLevelBlock = null;
        foreach (var block in snapshot.Blocks)
        {
            if (FindBlockY(block, blockIndex) is null)
                continue;

            targetTopLevelBlock = block;
            break;
        }

        if (targetTopLevelBlock is null)
            return;

        if (snapshot.IsLazyLayoutEnabled && !snapshot.IsTopLevelBlockMeasured(targetTopLevelBlock))
        {
            double top = targetTopLevelBlock.Bounds.Top;
            double bottom = Math.Max(top + 1, targetTopLevelBlock.Bounds.Bottom);
            QueueLazyLayoutForBand(
                snapshot,
                new LazyLayoutBand(top, bottom),
                preserveScrollAnchor: false);
        }

        double targetTop = FindBlockY(targetTopLevelBlock, blockIndex) ?? targetTopLevelBlock.Bounds.Top;
        if (_scroll is not null)
            _scroll.ChangeView(null, Math.Max(0, targetTop - 24), null, disableAnimation: true);
        else
            BringDocumentRectIntoExternalViewport(
                new Rect(0, Math.Max(0, targetTop - 24), 1, Math.Max(1, targetTopLevelBlock.Bounds.Height)),
                animate: false);
    }

    private static double? FindNearestScrollAnchor(LayoutSnapshot snapshot, int blockIndex, double offsetFromTop)
    {
        BlockBox? nearest = null;
        int bestDistance = int.MaxValue;
        foreach (var block in snapshot.Blocks)
        {
            int distance = Math.Abs(block.BlockIndex - blockIndex);
            if (distance >= bestDistance)
                continue;

            nearest = block;
            bestDistance = distance;
        }

        return nearest is null ? null : nearest.Bounds.Top - offsetFromTop;
    }

    private static double? FindBlockY(BlockBox box, int blockIndex)
    {
        if (box.BlockIndex == blockIndex) return box.Bounds.Top;
        if (box is Layout.Boxes.ListItemBox lib)
        {
            return FindBlockY(lib.Marker, blockIndex) ?? FindBlockY(lib.Content, blockIndex);
        }
        if (box is Layout.Boxes.StackBox sb)
        {
            foreach (var c in sb.Children)
            {
                if (FindBlockY(c, blockIndex) is { } y) return y;
            }
        }
        if (box is Layout.Boxes.TableBox tb)
        {
            foreach (var cell in tb.GetCellBoxes())
            {
                if (FindBlockY(cell, blockIndex) is { } y) return y;
            }
        }
        return null;
    }

    /// <summary>
    /// Handles internal fragment navigation URLs (e.g. <c>#footnote-def-1</c>,
    /// <c>#footnote-ref-1</c>) by scrolling to the target block directly.
    /// </summary>
    private bool HandleInternalAnchor(string url)
    {
        if (_snapshot is null) return false;
        // Resolve block index from footnote registry stored on the layout context.
        // The registry is embedded in the LayoutSnapshot's block tree; walk to find
        // the first InlineContainerBox whose block carries the matching footnote tag.
        // We look for a special metadata property set by FootnoteRenderer.
        //   #footnote-def-{order}  → scroll to the definition box
        //   #footnote-ref-{order}  → scroll to the inline citation box
        const string defPrefix = "#footnote-def-";
        const string refPrefix = "#footnote-ref-";
        bool isDef = url.StartsWith(defPrefix, StringComparison.OrdinalIgnoreCase);
        bool isRef = url.StartsWith(refPrefix, StringComparison.OrdinalIgnoreCase);
        if (isDef || isRef)
        {
            string orderStr = isDef ? url.Substring(defPrefix.Length) : url.Substring(refPrefix.Length);
            if (!int.TryParse(orderStr, out int order)) return false;

            // Walk blocks looking for the tagged block index stored in the
            // footnote index dictionary on the snapshot.
            int? targetIndex = isDef
                ? _snapshot.FootnoteDefBlock(order)
                : _snapshot.FootnoteRefBlock(order);
            if (targetIndex is { } idx)
            {
                ScrollToBlock(idx);
                return true;
            }
            return false;
        }

        if (url.StartsWith("#", StringComparison.Ordinal))
        {
            var id = Uri.UnescapeDataString(url.Substring(1));
            if (_snapshot.FragmentTargetBlock(id) is { } idx)
            {
                ScrollToBlock(idx);
                return true;
            }
        }

        return false;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_snapshot is null) return;
        bool ctrl = (Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down)
            == Windows.UI.Core.CoreVirtualKeyStates.Down;
        bool shift = (Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down)
            == Windows.UI.Core.CoreVirtualKeyStates.Down;

        switch (e.Key)
        {
            case VirtualKey.C when ctrl && _selection.IsActive:
                e.Handled = ExecuteSelectionCommand(MarkdownCommandKind.CopyRendered);
                return;
            case VirtualKey.A when MarkdownKeyboardInputPolicy.ShouldSelectAll(ctrl, IsSelectionEnabled):
                _selection.SetAnchor(DocumentPosition.Zero);
                _selection.ExtendTo(new DocumentPosition(int.MaxValue, int.MaxValue, int.MaxValue));
                e.Handled = true;
                return;
            case VirtualKey.F10 when shift && _selection.IsActive:
                e.Handled = ShowSelectionContextMenu(GetKeyboardContextMenuPoint());
                return;
            case VirtualKey.Application when _selection.IsActive:
                e.Handled = ShowSelectionContextMenu(GetKeyboardContextMenuPoint());
                return;
            case VirtualKey.Tab:
                e.Handled = MoveFocus(reverse: shift);
                return;
            case VirtualKey.Left:
            case VirtualKey.Right:
                e.Handled = HandleHorizontalArrowKey(e.Key);
                return;
            case VirtualKey.Home:
            case VirtualKey.End:
                e.Handled = ScrollFocusedHorizontalOverflowToEdge(e.Key == VirtualKey.End);
                return;
            case VirtualKey.Up:
            case VirtualKey.Down:
                e.Handled = MoveFocusSpatial(e.Key);
                return;
            case VirtualKey.Enter:
            case VirtualKey.Space when _focusedItemIndex >= 0:
                e.Handled = ActivateFocusedItem();
                return;
            case VirtualKey.Escape:
                if (_focusedItemIndex >= 0)
                {
                    _pendingLazyFocus = null;
                    _focusedItemIndex = -1;
                    UpdateFocusRing();
                    e.Handled = true;
                }
                else if (_selection.IsActive)
                {
                    _selection.Clear();
                    e.Handled = true;
                }
                return;
        }
    }

    private bool HandleHorizontalArrowKey(VirtualKey key)
    {
        HorizontalArrowHandlingOrder order =
            MarkdownKeyboardInputPolicy.GetHorizontalArrowHandlingOrder(
                GetCurrentFocusableIdentity()?.Kind);
        return order == HorizontalArrowHandlingOrder.OverflowThenSpatial
            ? ScrollFocusedHorizontalOverflow(key) || MoveFocusSpatial(key)
            : MoveFocusSpatial(key) || ScrollFocusedHorizontalOverflow(key);
    }

    private bool ScrollFocusedHorizontalOverflow(VirtualKey key)
    {
        if (_snapshot is not { } snapshot ||
            _focusableItems is not { } items ||
            _focusedItemIndex < 0 ||
            _focusedItemIndex >= items.Count ||
            !snapshot.TryGetHorizontalOverflowForBlockIndex(
                items[_focusedItemIndex].BlockIndex,
                out var overflow) ||
            overflow is null)
        {
            return false;
        }

        bool towardPhysicalRight = key == VirtualKey.Right;
        double logicalDelta = overflow.IsRightToLeft
            ? (towardPhysicalRight ? -48 : 48)
            : (towardPhysicalRight ? 48 : -48);
        double oldOffset = overflow.HorizontalOffset;
        if (!snapshot.TryScrollHorizontal(overflow, logicalDelta))
            return false;

        RefreshHorizontalOverflowVisualState(overflow, oldOffset);
        return true;
    }

    private bool ScrollFocusedHorizontalOverflowToEdge(bool end)
    {
        if (_snapshot is not { } snapshot ||
            _focusableItems is not { } items ||
            _focusedItemIndex < 0 ||
            _focusedItemIndex >= items.Count ||
            !snapshot.TryGetHorizontalOverflowForBlockIndex(
                items[_focusedItemIndex].BlockIndex,
                out var overflow) ||
            overflow is null)
        {
            return false;
        }

        double oldOffset = overflow.HorizontalOffset;
        double target = end
            ? Math.Max(0, overflow.HorizontalExtent - overflow.HorizontalViewport)
            : 0;
        if (!snapshot.TrySetHorizontalOffset(overflow, target))
            return false;

        RefreshHorizontalOverflowVisualState(overflow, oldOffset);
        return true;
    }

    /// <summary>
    /// Advances (or reverses) keyboard focus through the focusable items list.
    /// Returns true when focus is successfully moved (prevents Tab from leaving the control).
    /// </summary>
    private bool MoveFocus(bool reverse)
    {
        var items = _focusableItems;
        if (items is null || items.Count == 0) return false;

        int nextIndex = FocusNavigationHelper.MoveTab(items.Count, _focusedItemIndex, _focusResumeItemIndex, reverse);
        if (nextIndex < 0)
        {
            // Let Tab/Shift+Tab leave the control to the next focusable element.
            _pendingLazyFocus = null;
            _focusedItemIndex = -1;
            _focusResumeItemIndex = -1;
            UpdateFocusRing();
            SuppressHostedTabStopsForNativeTab();
            return false;
        }

        _focusedItemIndex = nextIndex;
        _focusResumeItemIndex = -1;
        return CommitFocusedItem(reverse);
    }

    private void SuppressHostedTabStopsForNativeTab()
    {
        var suppressed = new List<(Control Control, bool IsTabStop)>();
        var seen = new HashSet<Control>();
        foreach (var plan in _embedPlans)
        {
            if (plan.Realized is { } realized)
                SuppressHostedTabStops(realized, suppressed, seen);
        }
        foreach (var plan in _codeBlockActionPlans)
        {
            if (plan.Realized is { } realized)
                SuppressHostedTabStops(realized, suppressed, seen);
        }

        if (suppressed.Count == 0)
            return;

        DispatcherQueue.TryEnqueue(() =>
        {
            foreach (var (control, isTabStop) in suppressed)
                control.IsTabStop = isTabStop;
        });
    }

    private static void SuppressHostedTabStops(
        DependencyObject root,
        List<(Control Control, bool IsTabStop)> suppressed,
        HashSet<Control> seen)
    {
        if (root is Control control && seen.Add(control))
        {
            suppressed.Add((control, control.IsTabStop));
            control.IsTabStop = false;
        }

        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < childCount; i++)
        {
            SuppressHostedTabStops(VisualTreeHelper.GetChild(root, i), suppressed, seen);
        }
    }

    private bool MoveFocusSpatial(VirtualKey key)
    {
        var items = _focusableItems;
        if (items is null || items.Count == 0) return false;

        if (_focusedItemIndex < 0)
        {
            if (_focusResumeItemIndex < 0) return false;
            _focusedItemIndex = Math.Clamp(_focusResumeItemIndex, 0, items.Count - 1);
            _focusResumeItemIndex = -1;
            return CommitFocusedItem(reverse: false);
        }

        var map = BuildFocusableRectMap();
        int currentLocalIndex = -1;
        for (int i = 0; i < map.Count; i++)
        {
            if (map[i].Index == _focusedItemIndex)
            {
                currentLocalIndex = i;
                break;
            }
        }

        if (currentLocalIndex < 0) return false;

        var rects = new List<Rect>(map.Count);
        foreach (var entry in map) rects.Add(entry.Rect);

        var direction = key switch
        {
            VirtualKey.Left => Layout.FocusNavigationDirection.Left,
            VirtualKey.Right => Layout.FocusNavigationDirection.Right,
            VirtualKey.Up => Layout.FocusNavigationDirection.Up,
            VirtualKey.Down => Layout.FocusNavigationDirection.Down,
            _ => Layout.FocusNavigationDirection.Down,
        };

        int nextLocalIndex = FocusNavigationHelper.MoveSpatial(rects, currentLocalIndex, direction);
        if (nextLocalIndex < 0) return false;

        _focusedItemIndex = map[nextLocalIndex].Index;
        _focusResumeItemIndex = -1;
        return CommitFocusedItem(reverse: false);
    }

    private bool CommitFocusedItem(bool reverse)
    {
        var items = _focusableItems;
        LayoutSnapshot? snapshot = _snapshot;
        if (snapshot is null ||
            items is null ||
            _focusedItemIndex < 0 ||
            _focusedItemIndex >= items.Count)
        {
            _pendingLazyFocus = null;
            return false;
        }

        Layout.FocusableItem item = items[_focusedItemIndex];
        FocusTargetLayoutState layoutState = snapshot.TryGetFocusTargetLayoutState(
            item.BlockIndex,
            out LazyLayoutBand estimatedBand);
        if (layoutState is FocusTargetLayoutState.RequiresRealization or FocusTargetLayoutState.Busy)
        {
            // Keep native focus on the renderer while the target's top-level
            // block is measured. This consumes the Tab instead of allowing XAML
            // to advance beyond the renderer at a lazy-layout boundary.
            _pendingLazyFocus = new PendingLazyFocus(snapshot, item, reverse);
            _ = Focus(FocusState.Keyboard);
            HideFocusRing();

            if (layoutState == FocusTargetLayoutState.RequiresRealization)
            {
                ScrollLazyFocusTargetIntoView(estimatedBand, reverse);
                var realizationBand = LazyLayoutBand.FromViewport(
                    estimatedBand.Top,
                    Math.Max(1, estimatedBand.Bottom - estimatedBand.Top),
                    LazyLayoutOverscanPx);
                QueueLazyLayoutForBand(
                    snapshot,
                    realizationBand,
                    preserveScrollAnchor: _scroll is not null);
            }

            return true;
        }

        if (layoutState == FocusTargetLayoutState.Unavailable)
        {
            // A retiring snapshot or malformed custom block index must never
            // turn an already-consumed Tab into focus escape.
            _pendingLazyFocus = null;
            _ = Focus(FocusState.Keyboard);
            HideFocusRing();
            return true;
        }

        _pendingLazyFocus = null;
        bool scrolled = ScrollFocusedItemIntoView();
        FocusCurrentItem(reverse);
        if (scrolled)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_isUnloaded) return;
                RealizeVisibleEmbeds();
                FocusCurrentItem(reverse);
            });
        }
        return true;
    }

    private bool ResumePendingLazyFocus(LayoutSnapshot snapshot)
    {
        if (_pendingLazyFocus is not { } pending)
            return false;

        if (!ReferenceEquals(snapshot, pending.Snapshot))
        {
            _pendingLazyFocus = null;
            return false;
        }

        int index = FindFocusableItemIndex(_focusableItems, pending.Item);
        if (index < 0)
        {
            _pendingLazyFocus = null;
            _focusedItemIndex = -1;
            return false;
        }

        _focusedItemIndex = index;
        return CommitFocusedItem(pending.Reverse);
    }

    private Layout.FocusableItem? GetCurrentFocusableIdentity()
    {
        var items = _focusableItems;
        return items is not null &&
               _focusedItemIndex >= 0 &&
               _focusedItemIndex < items.Count
            ? items[_focusedItemIndex]
            : null;
    }

    private static int FindFocusableItemIndex(
        System.Collections.Generic.IReadOnlyList<Layout.FocusableItem>? items,
        Layout.FocusableItem target)
    {
        if (items is null)
            return -1;

        for (int index = 0; index < items.Count; index++)
        {
            Layout.FocusableItem candidate = items[index];
            if (candidate.BlockIndex == target.BlockIndex &&
                candidate.InlineIndex == target.InlineIndex &&
                candidate.Kind == target.Kind)
            {
                return index;
            }
        }

        return -1;
    }

    private void ScrollLazyFocusTargetIntoView(LazyLayoutBand band, bool reverse)
    {
        TryGetViewport(out double viewportTop, out double viewportHeight, out double viewportWidth);
        double viewportBottom = viewportTop + viewportHeight;
        const double margin = 24;
        if (band.Top >= viewportTop + margin && band.Bottom <= viewportBottom - margin)
            return;

        if (_scroll is not null)
        {
            double target = reverse
                ? band.Bottom - viewportHeight + margin
                : band.Top - margin;
            double maximum = Math.Max(0, (_snapshot?.Size.Height ?? band.Bottom) - viewportHeight);
            _scroll.ChangeView(
                null,
                Math.Clamp(target, 0, maximum),
                null,
                disableAnimation: false);
            return;
        }

        var targetRect = new Rect(
            0,
            band.Top,
            Math.Max(1, viewportWidth),
            Math.Max(1, band.Bottom - band.Top));
        BringDocumentRectIntoExternalViewport(
            targetRect,
            animate: true,
            alignmentRatio: reverse ? 1 : 0);
    }

    /// <summary>Fires LinkClick for the currently focused painted link.</summary>
    private bool ActivateFocusedItem()
    {
        if (_pendingLazyFocus is not null)
            return true;

        var items = _focusableItems;
        if (items is null || _focusedItemIndex < 0 || _focusedItemIndex >= items.Count)
            return false;

        var item = items[_focusedItemIndex];
        MarkdownInputModifiers modifiers = GetCurrentInputModifiers();
        if (item.IsVectorSemantic &&
            TryGetVectorSemanticForFocusable(item, out var vectorBox, out int semanticIndex) &&
            vectorBox.TryGetLink(semanticIndex, out var vectorLink) &&
            vectorLink is not null)
        {
            MarkdownVectorSemanticItem? semantic = vectorBox.GetSemanticItem(semanticIndex);
            return ActivateLinkTarget(
                new LinkTarget(
                    vectorLink.Target ?? string.Empty,
                    semantic?.Description,
                    semantic?.SourceSpan ?? vectorBox.Content.SourceSpan,
                    null,
                    vectorLink.Action,
                    vectorLink.External),
                MarkdownLinkInputKind.Keyboard,
                modifiers);
        }

        if (!item.IsLink)
            return TryFocusHostedElement(item, reverse: false);

        // Resolve either a text link or a linked inline image and use the same
        // activation path as pointer input.
        if (_snapshot is null) return false;
        var pos = new DocumentPosition(item.BlockIndex, item.InlineIndex, 0);
        if (FindLinkTargetAt(pos) is { } link)
        {
            return ActivateLinkTarget(link, MarkdownLinkInputKind.Keyboard, modifiers);
        }
        return false;
    }

    private void FocusCurrentItem(bool reverse = false)
    {
        if (_pendingLazyFocus is not null)
        {
            HideFocusRing();
            return;
        }

        var items = _focusableItems;
        if (items is null || _focusedItemIndex < 0 || _focusedItemIndex >= items.Count)
        {
            UpdateFocusRing();
            return;
        }

        var item = items[_focusedItemIndex];
        if (!item.IsLink && TryFocusHostedElement(item, reverse))
        {
            if (_focusRing is not null) _focusRing.Visibility = Visibility.Collapsed;
            return;
        }

        Focus(FocusState.Keyboard);
        UpdateFocusRing();
        NotifyFocusedItemAutomation();
    }

    private void NotifyFocusedItemAutomation()
    {
        // Virtual focus is observable through HasKeyboardFocus even when no UIA
        // client subscribes to focus events. Avoid entering UIA from the input
        // path unless an assistive-technology listener actually needs the event;
        // this also prevents a queued focus notification from racing a subsequent
        // key press and re-entering this provider while the client is polling it.
        if (!AutomationPeer.ListenerExists(AutomationEvents.AutomationFocusChanged))
            return;

        void Raise()
        {
            if (_pendingLazyFocus is not null ||
                !AutomationPeer.ListenerExists(AutomationEvents.AutomationFocusChanged))
                return;

            var peer = FrameworkElementAutomationPeer.FromElement(this) as MarkdownAutomationPeer
                       ?? FrameworkElementAutomationPeer.CreatePeerForElement(this) as MarkdownAutomationPeer;
            if (TryGetFocusedLink(out var inline, out var link))
            {
                peer?.RaiseFocusForLink(inline, link);
            }
            else if (TryGetFocusedLinkedImage(out var imageInline, out var image))
            {
                peer?.RaiseFocusForLinkedImage(imageInline, image);
            }
            else if (TryGetFocusedVectorSemantic(out var vectorBox, out int semanticIndex))
            {
                peer?.RaiseFocusForVectorSemantic(vectorBox, semanticIndex);
            }
            else if (TryGetFocusedHorizontalOverflow(out var overflow))
            {
                peer?.RaiseFocusForHorizontalOverflow(overflow);
            }
        }

        // Let WinUI finish the real focus transition to the renderer first,
        // then publish the virtual hyperlink focus event. If we raise inside
        // GotFocus, Narrator can observe the root document focus event last and
        // announce the whole renderer instead of the focused painted link.
        if (DispatcherQueue is { } dispatcher)
            dispatcher.TryEnqueue(Raise);
        else
            Raise();
    }

    /// <summary>
    /// Places or moves the focus-ring <see cref="Border"/> on the overlay to
    /// surround the currently focused item. Hides the ring if nothing is focused.
    /// </summary>
    private void UpdateFocusRing()
    {
        if (_pendingLazyFocus is not null)
        {
            HideFocusRing();
            return;
        }

        var items = _focusableItems;
        if (items is null || _focusedItemIndex < 0 || _focusedItemIndex >= items.Count)
        {
            HideFocusRing();
            return;
        }

        var item = items[_focusedItemIndex];
        if (!item.IsLink && !item.IsVectorSemantic && !item.IsHorizontalOverflow)
        {
            HideFocusRing();
            return;
        }

        var rect = GetFocusableItemRect(item);
        if (rect is not { Width: > 0, Height: > 0 } r)
        {
            HideFocusRing();
            return;
        }

        if (!EnsureFocusRing())
        {
            InvalidateInteractiveTextAdorner();
            return;
        }
        var focusRing = _focusRing!;

        var focusVisual = _themeSnapshot?.FocusVisualColor
                          ?? Windows.UI.Color.FromArgb(0xFF, 0x00, 0x78, 0xD4);
        // Allocate a new SolidColorBrush only when the focus color actually changes;
        // avoid per-keystroke GC pressure during Tab traversal.
        if (focusRing.BorderBrush is null || focusVisual != _focusRingBrushColor)
        {
            _focusRingBrushColor = focusVisual;
            focusRing.BorderBrush = new SolidColorBrush(focusVisual);
        }

        const double pad = 2.0;
        Canvas.SetLeft(focusRing, r.X - pad);
        Canvas.SetTop(focusRing, r.Y - pad);
        focusRing.Width  = r.Width  + pad * 2;
        focusRing.Height = r.Height + pad * 2;
        focusRing.Visibility = Visibility.Visible;
        InvalidateInteractiveTextAdorner();
    }

    private void HideFocusRing()
    {
        if (_focusRing is not null)
            _focusRing.Visibility = Visibility.Collapsed;

        InvalidateInteractiveTextAdorner();
    }

    private bool EnsureFocusRing()
    {
        if (_focusRing is not null)
            return true;

        if (_overlay is null || _isUnloaded || XamlRoot is null)
            return false;

        var focusRing = new Microsoft.UI.Xaml.Controls.Border
        {
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(3),
            IsHitTestVisible = false,
        };
        Canvas.SetZIndex(focusRing, 1);

        try
        {
            _overlay.Children.Add(focusRing);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            MarkdownDiagnostics.WriteLine($"[MarkdownRendererControl] focus ring attach failed: {ex.Message}");
            return false;
        }

        _focusRing = focusRing;
        return true;
    }

    private Rect? GetFocusableItemRect(Layout.FocusableItem item)
    {
        if (_snapshot is null) return null;
        if (item.IsHorizontalOverflow &&
            _snapshot.TryGetHorizontalOverflowForBlockIndex(item.BlockIndex, out var overflow) &&
            overflow is not null)
        {
            return overflow.HorizontalViewportBounds;
        }
        foreach (var b in _snapshot.Blocks)
        {
            if (b is Layout.Boxes.InlineContainerBox icb && icb.BlockIndex == item.BlockIndex)
                return icb.GetRunRect(item.InlineIndex);
            var nested = GetFocusableItemRectFromBlock(b, item);
            if (nested is not null) return nested;
        }
        return null;
    }

    private void RememberFocusResumePoint(Point documentPoint)
    {
        var map = BuildFocusableRectMap();
        if (map.Count == 0)
        {
            _focusResumeItemIndex = -1;
            return;
        }

        var rects = new List<Rect>(map.Count);
        foreach (var entry in map) rects.Add(entry.Rect);

        int nearest = FocusNavigationHelper.FindNearestIndex(rects, documentPoint);
        _focusResumeItemIndex = nearest >= 0 ? map[nearest].Index : -1;
    }

    private List<(int Index, Rect Rect)> BuildFocusableRectMap()
    {
        var result = new List<(int Index, Rect Rect)>();
        var items = _focusableItems;
        if (items is null) return result;

        for (int i = 0; i < items.Count; i++)
        {
            if (GetFocusableItemRect(items[i]) is { Width: > 0, Height: > 0 } rect)
                result.Add((i, rect));
        }

        return result;
    }

    private static Rect? GetFocusableItemRectFromBlock(BlockBox box, Layout.FocusableItem item)
    {
        if (item.IsHorizontalOverflow &&
            box.BlockIndex == item.BlockIndex &&
            box is IHorizontalOverflowBox overflow)
        {
            return overflow.HorizontalViewportBounds;
        }

        if (box is Layout.Boxes.DeclarativeHostedElementBox hosted &&
            item.IsDeclarativeHostedElement &&
            hosted.BlockIndex == item.BlockIndex)
        {
            return new Rect(
                hosted.Bounds.X + hosted.Margin.Left,
                hosted.Bounds.Y + hosted.Margin.Top,
                Math.Max(0, hosted.Bounds.Width - hosted.Margin.Left - hosted.Margin.Right),
                Math.Max(0, hosted.Bounds.Height - hosted.Margin.Top - hosted.Margin.Bottom));
        }
        if (box is Layout.Boxes.EmbedBox eb && eb.BlockIndex == item.BlockIndex)
        {
            return new Rect(
                eb.Bounds.X + eb.Margin.Left,
                eb.Bounds.Y + eb.Margin.Top,
                eb.Bounds.Width - eb.Margin.Left - eb.Margin.Right,
                eb.Bounds.Height - eb.Margin.Top - eb.Margin.Bottom);
        }
        if (box is Layout.Boxes.CodeBlockBox codeBlock && codeBlock.BlockIndex == item.BlockIndex)
        {
            if (item.IsCodeBlockCopy)
                return codeBlock.CopyButtonBounds;
        }
        if (box is Layout.Boxes.VectorSceneBox vector &&
            item.IsVectorSemantic &&
            vector.BlockIndex == item.BlockIndex)
        {
            return vector.GetSemanticBounds(item.InlineIndex);
        }
        if (box is Layout.Boxes.InlineContainerBox icb && icb.BlockIndex == item.BlockIndex)
            return icb.GetRunRect(item.InlineIndex);
        if (box is Layout.Boxes.ListItemBox lib)
        {
            return GetFocusableItemRectFromBlock(lib.Marker, item)
                ?? GetFocusableItemRectFromBlock(lib.Content, item);
        }
        if (box is Layout.Boxes.StackBox sb)
        {
            foreach (var c in sb.Children)
            {
                var r = GetFocusableItemRectFromBlock(c, item);
                if (r is not null) return r;
            }
        }
        if (box is Layout.Boxes.TableBox tb)
        {
            foreach (var cell in tb.GetCellBoxes())
            {
                var r = GetFocusableItemRectFromBlock(cell, item);
                if (r is not null) return r;
            }
        }
        return null;
    }

    /// <summary>Scrolls the focused item into view if it's outside the current viewport.</summary>
    private bool ScrollFocusedItemIntoView()
    {
        var items = _focusableItems;
        if (items is null || _focusedItemIndex < 0) return false;
        var item = items[_focusedItemIndex];
        if (GetFocusableItemRect(item) is not { } rect) return false;

        bool horizontalScrolled = false;
        if (_snapshot is { } snapshot &&
            snapshot.TryGetHorizontalOverflowForBlockIndex(item.BlockIndex, out var overflow) &&
            overflow is not null)
        {
            Rect localViewport = overflow.HorizontalViewportBounds;
            const double localMargin = 8.0;
            double physicalShift = 0;
            if (rect.Left < localViewport.Left + localMargin)
                physicalShift = localViewport.Left + localMargin - rect.Left;
            else if (rect.Right > localViewport.Right - localMargin)
                physicalShift = localViewport.Right - localMargin - rect.Right;

            if (Math.Abs(physicalShift) > 0.1)
            {
                double oldOffset = overflow.HorizontalOffset;
                double logicalShift = overflow.IsRightToLeft ? physicalShift : -physicalShift;
                if (snapshot.TrySetHorizontalOffset(overflow, oldOffset + logicalShift))
                {
                    RefreshHorizontalOverflowVisualState(overflow, oldOffset);
                    horizontalScrolled = true;
                    rect = GetFocusableItemRect(item) ?? rect;
                }
            }
        }

        TryGetViewport(out double top, out double viewportHeight, out _);
        double bottom = top + viewportHeight;
        const double margin = 24.0;

        if (rect.Top < top + margin)
            return (_scroll is not null
                ? _scroll.ChangeView(null, Math.Max(0, rect.Top - margin), null, disableAnimation: false)
                : BringDocumentRectIntoExternalViewport(rect, animate: true)) || horizontalScrolled;
        else if (rect.Bottom > bottom - margin)
            return (_scroll is not null
                ? _scroll.ChangeView(null, rect.Bottom - viewportHeight + margin, null, disableAnimation: false)
                : BringDocumentRectIntoExternalViewport(rect, animate: true, alignmentRatio: 1)) || horizontalScrolled;

        return horizontalScrolled;
    }

    private bool TryFocusHostedElement(Layout.FocusableItem item, bool reverse)
    {
        if (FindHostedElementForFocusable(item) is not { } element)
            return false;

        if (TryFocusHostedDescendant(element, reverse))
            return true;

        return element.Focus(FocusState.Keyboard);
    }

    private void RestoreFocusedCodeBlockActionIfNeeded(CodeBlockActionPlan plan)
    {
        if (FocusState == FocusState.Unfocused ||
            plan.Realized is null ||
            _focusableItems is not { } items ||
            _focusedItemIndex < 0 ||
            _focusedItemIndex >= items.Count)
        {
            return;
        }

        var focusedItem = items[_focusedItemIndex];
        if (focusedItem.IsCodeBlockCopy && focusedItem.BlockIndex == plan.Box.BlockIndex)
            _ = plan.Realized.Focus(FocusState.Keyboard);
    }

    private void PreserveLogicalFocusBeforeDerealizingCodeBlockAction(FrameworkElement element)
    {
        if (_isUnloaded || _isDisposed)
            return;

        DependencyObject? focused = null;
        try
        {
            if (XamlRoot is not null)
                focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        }
        catch
        {
        }

        if (focused is null || !IsElementWithin(focused, element))
            return;

        // Keep the renderer's logical focus index while the native copy button
        // is recycled. If the action comes back into the realize band,
        // RestoreFocusedCodeBlockActionIfNeeded transfers focus back to it.
        _ = Focus(FocusState.Keyboard);
        UpdateFocusRing();
    }

    private static bool TryMoveFocusWithinHostedEmbed(FrameworkElement host, object originalSource, bool reverse)
    {
        var focusables = CollectHostedFocusableControls(host);
        if (focusables.Count <= 1)
            return false;

        var current = FindCurrentHostedFocusable(host, originalSource, focusables);
        if (current is null)
            return false;

        int index = focusables.IndexOf(current);
        if (index < 0)
            return false;

        int next = reverse ? index - 1 : index + 1;
        if (next < 0 || next >= focusables.Count)
            return false;

        return focusables[next].Focus(FocusState.Keyboard);
    }

    private static bool TryFocusHostedDescendant(FrameworkElement host, bool reverse)
    {
        var focusables = CollectHostedFocusableControls(host);
        if (focusables.Count == 0)
            return false;

        var target = reverse ? focusables[^1] : focusables[0];
        return target.Focus(FocusState.Keyboard);
    }

    private static Control? FindCurrentHostedFocusable(
        FrameworkElement host,
        object originalSource,
        IReadOnlyList<Control> focusables)
    {
        DependencyObject? focused = null;
        try
        {
            if (host.XamlRoot is not null)
                focused = FocusManager.GetFocusedElement(host.XamlRoot) as DependencyObject;
        }
        catch
        {
        }

        var current = FindFocusableAncestorWithin(host, focused);
        if (current is not null && ContainsHostedFocusable(focusables, current))
            return current;

        current = FindFocusableAncestorWithin(host, originalSource as DependencyObject);
        return current is not null && ContainsHostedFocusable(focusables, current) ? current : null;
    }

    private static bool ContainsHostedFocusable(IReadOnlyList<Control> focusables, Control control)
    {
        for (int i = 0; i < focusables.Count; i++)
        {
            if (ReferenceEquals(focusables[i], control))
                return true;
        }

        return false;
    }

    private static Control? FindFocusableAncestorWithin(FrameworkElement host, DependencyObject? node)
    {
        while (node is not null)
        {
            if (ReferenceEquals(node, host))
                return node is Control hostControl && IsHostedFocusableControl(hostControl) ? hostControl : null;

            if (node is Control control && IsHostedFocusableControl(control))
                return control;

            DependencyObject? parent = null;
            try { parent = VisualTreeHelper.GetParent(node); }
            catch { }
            node = parent;
        }

        return null;
    }

    private static List<Control> CollectHostedFocusableControls(DependencyObject root)
    {
        var result = new List<Control>();
        CollectHostedFocusableControls(root, result);
        return result;
    }

    private static void CollectHostedFocusableControls(DependencyObject node, List<Control> result)
    {
        if (node is Control control && IsHostedFocusableControl(control))
            result.Add(control);

        int childCount;
        try { childCount = VisualTreeHelper.GetChildrenCount(node); }
        catch { return; }

        for (int i = 0; i < childCount; i++)
        {
            DependencyObject child;
            try { child = VisualTreeHelper.GetChild(node, i); }
            catch { continue; }
            CollectHostedFocusableControls(child, result);
        }
    }

    private static bool IsHostedFocusableControl(Control control) =>
        control.Visibility == Visibility.Visible &&
        control.IsEnabled &&
        control.IsTabStop;

    private FrameworkElement? FindHostedElementForFocusable(Layout.FocusableItem item)
    {
        if (_snapshot is null) return null;
        foreach (var block in _snapshot.Blocks)
        {
            var found = FindHostedElementForFocusable(block, item);
            if (found is not null) return found;
        }

        return null;
    }

    private bool TrySetFocusedItemForHostedElement(FrameworkElement element)
    {
        var items = _focusableItems;
        if (items is null) return false;
        for (int i = 0; i < items.Count; i++)
        {
            if (ReferenceEquals(FindHostedElementForFocusable(items[i]), element))
            {
                _focusedItemIndex = i;
                return true;
            }
        }

        return false;
    }

    private static FrameworkElement? FindHostedElementForFocusable(BlockBox box, Layout.FocusableItem item)
    {
        switch (box)
        {
            case Layout.Boxes.DeclarativeHostedElementBox hosted
                when item.IsDeclarativeHostedElement && hosted.BlockIndex == item.BlockIndex:
                return hosted.RealizedElement;
            case Layout.Boxes.EmbedBox eb when item.IsBlockEmbed && eb.BlockIndex == item.BlockIndex:
                return eb.RealizedElement;
            case Layout.Boxes.CodeBlockBox codeBlock when codeBlock.BlockIndex == item.BlockIndex:
                if (item.IsCodeBlockCopy)
                    return codeBlock.RealizedCopyButton;
                return null;
            case Layout.Boxes.InlineContainerBox icb when item.IsInlineEmbed && icb.BlockIndex == item.BlockIndex:
                foreach (var run in icb.Runs)
                {
                    if (run.InlineIndex == item.InlineIndex && run is InlineEmbedRun embed)
                        return embed.RealizedElement;
                }
                return null;
            case Layout.Boxes.ListItemBox lib:
                return FindHostedElementForFocusable(lib.Marker, item)
                       ?? FindHostedElementForFocusable(lib.Content, item);
            case Layout.Boxes.TableBox tb:
                foreach (var cell in tb.GetCellBoxes())
                {
                    var found = FindHostedElementForFocusable(cell, item);
                    if (found is not null) return found;
                }
                return null;
            case Layout.Boxes.StackBox sb:
                foreach (var child in sb.Children)
                {
                    var found = FindHostedElementForFocusable(child, item);
                    if (found is not null) return found;
                }
                return null;
            default:
                return null;
        }
    }

    private bool TryGetVectorPointerState(
        Point point,
        out bool linked,
        out bool selectable)
    {
        if (_snapshot is not null)
        {
            foreach (BlockBox block in _snapshot.Blocks)
            {
                if (TryGetVectorPointerStateInBlock(block, point, out linked, out selectable))
                    return true;
            }
        }

        linked = false;
        selectable = false;
        return false;
    }

    private static bool TryGetVectorPointerStateInBlock(
        BlockBox box,
        Point point,
        out bool linked,
        out bool selectable)
    {
        switch (box)
        {
            case Layout.Boxes.VectorSceneBox vector when vector.ContainsSceneViewport(point):
                linked = vector.TryGetLinkAt(point, out _);
                selectable = vector.IsSelectableAt(point);
                return true;
            case Layout.Boxes.ListItemBox listItem:
                if (TryGetVectorPointerStateInBlock(listItem.Marker, point, out linked, out selectable))
                    return true;
                return TryGetVectorPointerStateInBlock(listItem.Content, point, out linked, out selectable);
            case Layout.Boxes.TableBox table:
                foreach (Layout.Boxes.InlineContainerBox cell in table.GetCellBoxes())
                {
                    if (TryGetVectorPointerStateInBlock(cell, point, out linked, out selectable))
                        return true;
                }
                break;
            case Layout.Boxes.StackBox stack:
                foreach (BlockBox child in stack.Children)
                {
                    if (TryGetVectorPointerStateInBlock(child, point, out linked, out selectable))
                        return true;
                }
                break;
        }

        linked = false;
        selectable = false;
        return false;
    }

    private LinkTarget? FindVectorLinkTargetAtPoint(Point point)
    {
        if (_snapshot is null)
            return null;

        foreach (BlockBox block in _snapshot.Blocks)
        {
            if (TryGetVectorLinkAtPointInBlock(block, point, out var vector, out var link))
            {
                MarkdownVectorSemanticItem? semantic = vector.GetSemanticItem(link.SemanticIndex);
                return new LinkTarget(
                    link.Target ?? string.Empty,
                    semantic?.Description,
                    semantic?.SourceSpan ?? vector.Content.SourceSpan,
                    null,
                    link.Action,
                    link.External);
            }
        }

        return null;
    }

    private static bool TryGetVectorLinkAtPointInBlock(
        BlockBox box,
        Point point,
        out Layout.Boxes.VectorSceneBox vector,
        out MarkdownVectorLinkAction link)
    {
        switch (box)
        {
            case Layout.Boxes.VectorSceneBox candidate
                when candidate.TryGetLinkAt(point, out var candidateLink) && candidateLink is not null:
                vector = candidate;
                link = candidateLink;
                return true;
            case Layout.Boxes.ListItemBox listItem:
                return TryGetVectorLinkAtPointInBlock(listItem.Marker, point, out vector, out link) ||
                       TryGetVectorLinkAtPointInBlock(listItem.Content, point, out vector, out link);
            case Layout.Boxes.TableBox table:
                foreach (Layout.Boxes.InlineContainerBox cell in table.GetCellBoxes())
                {
                    if (TryGetVectorLinkAtPointInBlock(cell, point, out vector, out link))
                        return true;
                }
                break;
            case Layout.Boxes.StackBox stack:
                foreach (BlockBox child in stack.Children)
                {
                    if (TryGetVectorLinkAtPointInBlock(child, point, out vector, out link))
                        return true;
                }
                break;
        }

        vector = null!;
        link = null!;
        return false;
    }

    private LinkTarget? FindLinkTargetAt(DocumentPosition pos, Point? point = null)
    {
        if (_snapshot is null) return null;
        foreach (var b in _snapshot.Blocks)
        {
            if (FindLinkTargetInBlock(b, pos, point) is { } found) return found;
        }
        return null;
    }

    private bool TryGetFocusedLink(out Layout.Boxes.InlineContainerBox inline, out LinkRun link)
    {
        inline = null!;
        link = null!;

        var items = _focusableItems;
        if (items is null || _focusedItemIndex < 0 || _focusedItemIndex >= items.Count)
            return false;

        return TryGetLinkForFocusable(items[_focusedItemIndex], out inline, out link);
    }

    private bool TryGetFocusedLinkedImage(
        out Layout.Boxes.InlineContainerBox inline,
        out InlineImageRun image)
    {
        inline = null!;
        image = null!;

        var items = _focusableItems;
        if (items is null || _focusedItemIndex < 0 || _focusedItemIndex >= items.Count)
            return false;

        return TryGetLinkedImageForFocusable(items[_focusedItemIndex], out inline, out image);
    }

    private bool TryGetFocusedVectorSemantic(
        out Layout.Boxes.VectorSceneBox box,
        out int semanticIndex)
    {
        box = null!;
        semanticIndex = -1;
        var items = _focusableItems;
        if (items is null || _focusedItemIndex < 0 || _focusedItemIndex >= items.Count)
            return false;

        return TryGetVectorSemanticForFocusable(items[_focusedItemIndex], out box, out semanticIndex);
    }

    private bool TryGetFocusedHorizontalOverflow(out IHorizontalOverflowBox overflow)
    {
        overflow = null!;
        var items = _focusableItems;
        if (items is null ||
            _snapshot is null ||
            _focusedItemIndex < 0 ||
            _focusedItemIndex >= items.Count ||
            !items[_focusedItemIndex].IsHorizontalOverflow ||
            !_snapshot.TryGetHorizontalOverflowForBlockIndex(
                items[_focusedItemIndex].BlockIndex,
                out IHorizontalOverflowBox? focusedOverflow) ||
            focusedOverflow is null)
        {
            return false;
        }

        overflow = focusedOverflow;
        return true;
    }

    private bool TryGetFocusableIndexForLink(LinkRun run, out int index)
    {
        index = -1;
        var items = _focusableItems;
        if (items is null) return false;

        int equivalentIndex = -1;

        for (int i = 0; i < items.Count; i++)
        {
            if (!items[i].IsLink)
                continue;

            if (!TryGetLinkForFocusable(items[i], out _, out var link))
                continue;

            if (ReferenceEquals(link, run))
            {
                index = i;
                return true;
            }

            if (equivalentIndex < 0 && AreEquivalentLinks(link, run))
                equivalentIndex = i;
        }

        index = equivalentIndex;
        return index >= 0;
    }

    private bool TryGetFocusableIndexForLinkedImage(InlineImageRun run, out int index)
    {
        index = -1;
        var items = _focusableItems;
        if (items is null) return false;

        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].IsLink &&
                TryGetLinkedImageForFocusable(items[i], out _, out var image) &&
                ReferenceEquals(image, run))
            {
                index = i;
                return true;
            }
        }

        return false;
    }

    private bool TryGetFocusableIndexForVectorSemantic(
        Layout.Boxes.VectorSceneBox box,
        int semanticIndex,
        out int index)
    {
        index = -1;
        var items = _focusableItems;
        if (items is null)
            return false;

        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].IsVectorSemantic &&
                items[i].BlockIndex == box.BlockIndex &&
                items[i].InlineIndex == semanticIndex &&
                TryGetVectorSemanticForFocusable(items[i], out var candidate, out _) &&
                ReferenceEquals(candidate, box))
            {
                index = i;
                return true;
            }
        }

        return false;
    }

    private bool TryGetFocusableIndexForHorizontalOverflow(
        IHorizontalOverflowBox overflow,
        out int index)
    {
        var items = _focusableItems;
        var snapshot = _snapshot;
        if (items is null || snapshot is null)
        {
            index = -1;
            return false;
        }

        return TryFindHorizontalOverflowFocusableIndex(snapshot, items, overflow, out index);
    }

    internal static bool TryFindHorizontalOverflowFocusableIndex(
        LayoutSnapshot snapshot,
        IReadOnlyList<Layout.FocusableItem> items,
        IHorizontalOverflowBox overflow,
        out int index)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(overflow);
        index = -1;

        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].IsHorizontalOverflow &&
                snapshot.TryGetHorizontalOverflowForBlockIndex(
                    items[i].BlockIndex,
                    out IHorizontalOverflowBox? candidate) &&
                ReferenceEquals(candidate, overflow))
            {
                index = i;
                return true;
            }
        }

        return false;
    }

    private bool TryGetVectorSemanticForFocusable(
        Layout.FocusableItem item,
        out Layout.Boxes.VectorSceneBox vector,
        out int semanticIndex)
    {
        vector = null!;
        semanticIndex = -1;
        if (!item.IsVectorSemantic || _snapshot is null)
            return false;

        foreach (BlockBox block in _snapshot.Blocks)
        {
            if (TryGetVectorSemanticForFocusable(block, item, out vector, out semanticIndex))
                return true;
        }

        return false;
    }

    private static bool TryGetVectorSemanticForFocusable(
        BlockBox box,
        Layout.FocusableItem item,
        out Layout.Boxes.VectorSceneBox vector,
        out int semanticIndex)
    {
        vector = null!;
        semanticIndex = -1;
        switch (box)
        {
            case Layout.Boxes.VectorSceneBox candidate
                when candidate.BlockIndex == item.BlockIndex &&
                     candidate.GetSemanticItem(item.InlineIndex) is { } semantic &&
                     MarkdownVectorSemanticPolicy.IsKeyboardFocusable(semantic.Flags):
                vector = candidate;
                semanticIndex = item.InlineIndex;
                return true;
            case Layout.Boxes.ListItemBox listItem:
                return TryGetVectorSemanticForFocusable(listItem.Marker, item, out vector, out semanticIndex) ||
                       TryGetVectorSemanticForFocusable(listItem.Content, item, out vector, out semanticIndex);
            case Layout.Boxes.TableBox table:
                foreach (Layout.Boxes.InlineContainerBox cell in table.GetCellBoxes())
                {
                    if (TryGetVectorSemanticForFocusable(cell, item, out vector, out semanticIndex))
                        return true;
                }
                return false;
            case Layout.Boxes.StackBox stack:
                foreach (BlockBox child in stack.Children)
                {
                    if (TryGetVectorSemanticForFocusable(child, item, out vector, out semanticIndex))
                        return true;
                }
                return false;
            default:
                return false;
        }
    }

    private static bool AreEquivalentLinks(LinkRun left, LinkRun right) =>
        ReferenceEquals(left, right) ||
        (left.IsDisclosure && right.IsDisclosure &&
         string.Equals(left.DisclosureId, right.DisclosureId, StringComparison.Ordinal)) ||
        (string.Equals(left.Text, right.Text, StringComparison.Ordinal) &&
         string.Equals(left.Url, right.Url, StringComparison.Ordinal) &&
         string.Equals(left.Title, right.Title, StringComparison.Ordinal));

    private bool TryGetLinkForFocusable(Layout.FocusableItem item, out Layout.Boxes.InlineContainerBox inline, out LinkRun link)
    {
        inline = null!;
        link = null!;
        if (!item.IsLink || _snapshot is null)
            return false;

        foreach (var b in _snapshot.Blocks)
        {
            if (TryGetLinkForFocusable(b, item, out inline, out link))
                return true;
        }

        return false;
    }

    private static bool TryGetLinkForFocusable(
        BlockBox box,
        Layout.FocusableItem item,
        out Layout.Boxes.InlineContainerBox inline,
        out LinkRun link)
    {
        inline = null!;
        link = null!;

        switch (box)
        {
            case Layout.Boxes.InlineContainerBox icb when icb.BlockIndex == item.BlockIndex:
                foreach (var r in icb.Runs)
                {
                    if (r.InlineIndex == item.InlineIndex && r is LinkRun lr)
                    {
                        inline = icb;
                        link = lr;
                        return true;
                    }
                }
                return false;
            case Layout.Boxes.ListItemBox lib:
                return TryGetLinkForFocusable(lib.Marker, item, out inline, out link)
                       || TryGetLinkForFocusable(lib.Content, item, out inline, out link);
            case Layout.Boxes.TableBox tb:
                foreach (var cell in tb.GetCellBoxes())
                {
                    if (TryGetLinkForFocusable(cell, item, out inline, out link))
                        return true;
                }
                return false;
            case Layout.Boxes.StackBox sb:
                foreach (var c in sb.Children)
                {
                    if (TryGetLinkForFocusable(c, item, out inline, out link))
                        return true;
                }
                return false;
            default:
                return false;
        }
    }

    private bool TryGetLinkedImageForFocusable(
        Layout.FocusableItem item,
        out Layout.Boxes.InlineContainerBox inline,
        out InlineImageRun image)
    {
        inline = null!;
        image = null!;
        if (!item.IsLink || _snapshot is null)
            return false;

        foreach (var block in _snapshot.Blocks)
        {
            if (TryGetLinkedImageForFocusable(block, item, out inline, out image))
                return true;
        }

        return false;
    }

    private static bool TryGetLinkedImageForFocusable(
        BlockBox box,
        Layout.FocusableItem item,
        out Layout.Boxes.InlineContainerBox inline,
        out InlineImageRun image)
    {
        inline = null!;
        image = null!;

        switch (box)
        {
            case Layout.Boxes.InlineContainerBox icb when icb.BlockIndex == item.BlockIndex:
                foreach (var run in icb.Runs)
                {
                    if (run.InlineIndex == item.InlineIndex &&
                        run is InlineImageRun { IsLinked: true } linkedImage)
                    {
                        inline = icb;
                        image = linkedImage;
                        return true;
                    }
                }
                return false;
            case Layout.Boxes.ListItemBox listItem:
                return TryGetLinkedImageForFocusable(listItem.Marker, item, out inline, out image) ||
                       TryGetLinkedImageForFocusable(listItem.Content, item, out inline, out image);
            case Layout.Boxes.TableBox table:
                foreach (var cell in table.GetCellBoxes())
                {
                    if (TryGetLinkedImageForFocusable(cell, item, out inline, out image))
                        return true;
                }
                return false;
            case Layout.Boxes.StackBox stack:
                foreach (var child in stack.Children)
                {
                    if (TryGetLinkedImageForFocusable(child, item, out inline, out image))
                        return true;
                }
                return false;
            default:
                return false;
        }
    }

    // ---- Word / line selection helpers ----

    /// <summary>
    /// Expands selection to the word (maximal non-whitespace run) that contains
    /// <paramref name="pos"/> in its inline container.
    /// Returns the (start, end) document positions of the selected word.
    /// </summary>
    private (DocumentPosition Start, DocumentPosition End) ExpandSelectionToWord(LayoutSnapshot snapshot, DocumentPosition pos)
    {
        var icb = FindInlineContainerAt(snapshot, pos.BlockIndex);
        if (icb is null)
        {
            // Block has no inline container (code block, embed row, etc.).
            // Set anchor at the click position so subsequent drag ExtendTo calls
            // always have a valid anchor; this makes the selection empty (start==end)
            // while allowing drag to extend from this point.
            _selection.SetAnchor(pos);
            // No _canvas.Invalidate(): selection is rendered on the XAML overlay only.
            return (pos, pos);
        }
        var (start, end) = icb.GetWordBoundaries(pos);
        _selection.SetAnchor(start);
        _selection.ExtendTo(end);
        // No _canvas.Invalidate(): selection is rendered on the XAML overlay only.
        return (start, end);
    }

    /// <summary>
    /// Expands selection to the entire inline container (paragraph/heading line)
    /// that contains <paramref name="pos"/>.
    /// Returns the (start, end) document positions of the selected block.
    /// </summary>
    private (DocumentPosition Start, DocumentPosition End) ExpandSelectionToBlock(LayoutSnapshot snapshot, DocumentPosition pos)
    {
        var icb = FindInlineContainerAt(snapshot, pos.BlockIndex);
        if (icb is null)
        {
            // Block has no inline container (code block, embed row, etc.).
            // Set anchor at the click position so subsequent drag ExtendTo calls
            // always have a valid anchor; this makes the selection empty (start==end)
            // while allowing drag to extend from this point.
            _selection.SetAnchor(pos);
            // No _canvas.Invalidate(): selection is rendered on the XAML overlay only.
            return (pos, pos);
        }
        var (start, end) = icb.GetBlockBoundaries();
        _selection.SetAnchor(start);
        _selection.ExtendTo(end);
        // No _canvas.Invalidate(): selection is rendered on the XAML overlay only.
        return (start, end);
    }

    private static Layout.Boxes.InlineContainerBox? FindInlineContainerAt(LayoutSnapshot snapshot, int blockIndex)
    {
        foreach (var b in snapshot.Blocks)
        {
            var found = FindIcbInBlock(b, blockIndex);
            if (found is not null) return found;
        }
        return null;
    }

    private static Layout.Boxes.InlineContainerBox? FindIcbInBlock(BlockBox box, int blockIndex)
    {
        if (box is Layout.Boxes.InlineContainerBox icb && icb.BlockIndex == blockIndex) return icb;
        if (box is Layout.Boxes.CodeBlockBox codeBlock)
        {
            foreach (var chunk in codeBlock.Chunks)
            {
                if (chunk.BlockIndex == blockIndex) return chunk;
            }
        }
        if (box is Layout.Boxes.ListItemBox lib)
            return FindIcbInBlock(lib.Marker, blockIndex) ?? FindIcbInBlock(lib.Content, blockIndex);
        if (box is Layout.Boxes.StackBox sb)
        {
            foreach (var c in sb.Children)
            {
                var found = FindIcbInBlock(c, blockIndex);
                if (found is not null) return found;
            }
        }
        if (box is Layout.Boxes.TableBox tb)
        {
            foreach (var cell in tb.GetCellBoxes())
            {
                var found = FindIcbInBlock(cell, blockIndex);
                if (found is not null) return found;
            }
        }
        return null;
    }

    // ---- Right-click context menu ----

    private void OnRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (_canvas is not null)
            e.Handled |= DispatchRecognizedGesture(PointerInputKind.RightTap, e.GetPosition(_canvas), e.PointerDeviceType);
    }

    private void ProcessRightTapped(ref PointerInput e)
    {
        if (_canvas is null) return;
        var pt = e.GesturePosition;
        bool touchOrPen = e.GestureDevice is Microsoft.UI.Input.PointerDeviceType.Touch or
            Microsoft.UI.Input.PointerDeviceType.Pen;
        if (touchOrPen && (!_selection.IsActive || !IsPointInsideSelection(pt)))
        {
            TrySelectWordForTouchContext(pt);
        }
        e.Handled = ShowSelectionContextMenu(pt, touchSelection: touchOrPen);
    }

    private bool ShowSelectionContextMenu(Point placementPoint, bool touchSelection = false)
    {
        if (_canvas is null)
        {
            return false;
        }

        ContextMenuTarget target = FindContextMenuTarget(placementPoint);
        var menu = new MenuFlyout();
        _selectionContextMenu = menu;
        _contextMenuOpen = true;
        if (touchSelection)
            _touchSelection.OpenContextMenu();
        menu.Closed += (_, _) =>
        {
            _contextMenuOpen = false;
            if (ReferenceEquals(_selectionContextMenu, menu))
                _selectionContextMenu = null;
            if (touchSelection)
                _touchSelection.CloseContextMenu(_selection.IsActive);
            UpdateSelectionHandles();
        };

        bool hasSelectionItems = _selection.IsActive;
        if (hasSelectionItems)
        {
            var copyItem = new MenuFlyoutItem
            {
                Text = ResolveLocalizedString(
                    MarkdownStringKeys.Copy,
                    MarkdownLocalizedStrings.ContextMenuCopy),
                IsEnabled = CanExecuteSelectionCommandOrDefault(
                    MarkdownCommandKind.CopyRendered),
            };
            AutomationProperties.SetAutomationId(copyItem, "MarkdownContextCopy");
            copyItem.Click += (_, _) =>
                ExecuteSelectionCommand(MarkdownCommandKind.CopyRendered);
            menu.Items.Add(copyItem);

            var copyMarkdownItem = new MenuFlyoutItem
            {
                Text = ResolveLocalizedString(
                    MarkdownStringKeys.CopyMarkdown,
                    MarkdownLocalizedStrings.ContextMenuCopyMarkdown),
                IsEnabled = CanExecuteSelectionCommandOrDefault(
                    MarkdownCommandKind.CopyMarkdown),
            };
            AutomationProperties.SetAutomationId(copyMarkdownItem, "MarkdownContextCopyMarkdown");
            copyMarkdownItem.Click += (_, _) =>
                ExecuteSelectionCommand(MarkdownCommandKind.CopyMarkdown);
            menu.Items.Add(copyMarkdownItem);
        }

        bool hasTargetItems = AddTargetContextMenuItems(menu, target);
        if ((hasSelectionItems || hasTargetItems) && menu.Items.Count > 0)
            menu.Items.Add(new MenuFlyoutSeparator());

        var selectAllItem = new MenuFlyoutItem
        {
            Text = ResolveLocalizedString(
                MarkdownStringKeys.SelectAll,
                MarkdownLocalizedStrings.ContextMenuSelectAll),
            IsEnabled = IsSelectionEnabled,
        };
        AutomationProperties.SetAutomationId(selectAllItem, "MarkdownContextSelectAll");
        selectAllItem.Click += (_, _) =>
        {
            if (!IsSelectionEnabled) return;
            _touchSelection.Reset();
            _selection.SetAnchor(DocumentPosition.Zero);
            _selection.ExtendTo(new DocumentPosition(int.MaxValue, int.MaxValue, int.MaxValue));
        };
        menu.Items.Add(selectAllItem);

        try
        {
            menu.ShowAt(_canvas, placementPoint);
            return true;
        }
        catch (Exception ex)
        {
            _contextMenuOpen = false;
            if (ReferenceEquals(_selectionContextMenu, menu))
                _selectionContextMenu = null;
            if (touchSelection)
                _touchSelection.CloseContextMenu(_selection.IsActive);
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] context menu failed: {ex.Message}");
            return false;
        }
    }

    private void CloseSelectionContextMenu()
    {
        MenuFlyout? menu = _selectionContextMenu;
        _selectionContextMenu = null;
        _contextMenuOpen = false;
        if (menu is null)
            return;

        try { menu.Hide(); }
        catch { }
    }

    private bool AddTargetContextMenuItems(MenuFlyout menu, ContextMenuTarget target)
    {
        bool added = false;
        if (target.Link is { } link && !string.IsNullOrWhiteSpace(link.Url))
        {
            var action = new MarkdownTargetCommand(
                MarkdownCommandKind.CopyLink,
                link.SourceSpan,
                link.Url);
            var item = new MenuFlyoutItem
            {
                Text = ResolveLocalizedString(
                    MarkdownStringKeys.CopyLink,
                    MarkdownLocalizedStrings.ContextMenuCopyLink),
                IsEnabled = CanExecuteTargetCommandOrDefault(action, hasDefault: true),
            };
            AutomationProperties.SetAutomationId(item, "MarkdownContextCopyLink");
            item.Click += async (_, _) =>
                await ExecuteTargetCommandOrDefaultAsync(action, target).ConfigureAwait(true);
            menu.Items.Add(item);
            added = true;
        }

        if (target.Image is not null)
        {
            var action = new MarkdownTargetCommand(
                MarkdownCommandKind.CopyImage,
                target.ImageSourceRange,
                target.Image.Url);
            var item = new MenuFlyoutItem
            {
                Text = ResolveLocalizedString(
                    MarkdownStringKeys.CopyImage,
                    MarkdownLocalizedStrings.ContextMenuCopyImage),
                IsEnabled = CanExecuteTargetCommandOrDefault(
                    action,
                    hasDefault: !string.IsNullOrWhiteSpace(target.Image.Url) ||
                                target.Image.Bitmap is not null),
            };
            AutomationProperties.SetAutomationId(item, "MarkdownContextCopyImage");
            item.Click += async (_, _) =>
                await ExecuteTargetCommandOrDefaultAsync(action, target).ConfigureAwait(true);
            menu.Items.Add(item);
            added = true;
        }

        if (target.CodeBlock is not null)
        {
            var action = new MarkdownTargetCommand(
                MarkdownCommandKind.CopyCode,
                GetSourceRange(target.CodeBlock),
                Language: target.CodeBlock.CodeLanguage);
            var item = new MenuFlyoutItem
            {
                Text = ResolveLocalizedString(
                    MarkdownStringKeys.CopyCode,
                    MarkdownLocalizedStrings.ContextMenuCopyCode),
                IsEnabled = CanExecuteTargetCommandOrDefault(
                    action,
                    hasDefault: target.CodeBlock.CodeText.Length > 0),
            };
            AutomationProperties.SetAutomationId(item, "MarkdownContextCopyCode");
            item.Click += async (_, _) =>
                await ExecuteTargetCommandOrDefaultAsync(action, target).ConfigureAwait(true);
            menu.Items.Add(item);
            added = true;
        }

        if (target.Table is not null)
        {
            var action = new MarkdownTargetCommand(
                MarkdownCommandKind.CopyTable,
                GetSourceRange(target.Table));
            var item = new MenuFlyoutItem
            {
                Text = ResolveLocalizedString(
                    MarkdownStringKeys.CopyTable,
                    MarkdownLocalizedStrings.ContextMenuCopyTable),
                IsEnabled = CanExecuteTargetCommandOrDefault(
                    action,
                    hasDefault: target.Table.RowCount > 0),
            };
            AutomationProperties.SetAutomationId(item, "MarkdownContextCopyTable");
            item.Click += async (_, _) =>
                await ExecuteTargetCommandOrDefaultAsync(action, target).ConfigureAwait(true);
            menu.Items.Add(item);
            added = true;
        }

        return added;
    }

    private bool CanExecuteTargetCommandOrDefault(
        MarkdownTargetCommand action,
        bool hasDefault)
    {
        try
        {
            bool canExecute = MarkdownTargetCommandDispatcher.CanExecute(
                CommandProvider,
                action,
                out bool isProvided);
            return isProvided ? canExecute : hasDefault;
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] Host command '{action.Kind}' query failed: {ex.Message}");
            return hasDefault;
        }
    }

    private async Task<bool> ExecuteTargetCommandOrDefaultAsync(
        MarkdownTargetCommand action,
        ContextMenuTarget target)
    {
        try
        {
            MarkdownTargetCommandDispatchResult dispatch =
                MarkdownTargetCommandDispatcher.Execute(CommandProvider, action);
            if (dispatch == MarkdownTargetCommandDispatchResult.Executed)
                return true;
            if (dispatch == MarkdownTargetCommandDispatchResult.Disabled)
                return false;
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownRendererControl] Host command '{action.Kind}' failed: {ex.Message}");
        }

        return action.Kind switch
        {
            MarkdownCommandKind.CopyLink => CopyTextTargetToClipboard(action.Target),
            MarkdownCommandKind.CopyImage when target.Image is not null =>
                await CopyImageTargetToClipboardAsync(target.Image).ConfigureAwait(true),
            MarkdownCommandKind.CopyCode when target.CodeBlock is not null =>
                CopyCodeTargetToClipboard(target.CodeBlock),
            MarkdownCommandKind.CopyTable when target.Table is not null =>
                CopyTableTargetToClipboard(target.Table),
            _ => false,
        };
    }

    private static bool CopyTextTargetToClipboard(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        try
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(text);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool CopyCodeTargetToClipboard(Layout.Boxes.CodeBlockBox codeBlock)
    {
        bool succeeded = CopyTextTargetToClipboard(codeBlock.CodeText);
        RaiseCopyCompleted(MarkdownCopyKind.CodeBlock, succeeded);
        return succeeded;
    }

    private static async Task<bool> CopyImageTargetToClipboardAsync(
        Layout.Boxes.ImageBox image)
    {
        if (image.Bitmap is null && string.IsNullOrWhiteSpace(image.Url))
            return false;

        try
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            using var stream = new InMemoryRandomAccessStream();
            bool hasBitmap = false;
            if (image.Bitmap is { } bitmap)
            {
                await bitmap.SaveAsync(
                    stream,
                    CanvasBitmapFileFormat.Png,
                    1f);
                stream.Seek(0);
                package.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
                hasBitmap = true;
            }
            else if (Uri.TryCreate(image.Url, UriKind.Absolute, out Uri? uri) &&
                     uri.Scheme is "http" or "https" or "file" or "ms-appx")
            {
                package.SetBitmap(RandomAccessStreamReference.CreateFromUri(uri));
                hasBitmap = true;
            }

            if (!string.IsNullOrWhiteSpace(image.Url))
                package.SetText(image.Url);
            if (!hasBitmap && string.IsNullOrWhiteSpace(image.Url))
                return false;

            Clipboard.SetContent(package);
            Clipboard.Flush();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool CopyTableTargetToClipboard(Layout.Boxes.TableBox table)
    {
        MarkdownTableClipboardPayload payload = BuildTableClipboardPayload(table);
        if (string.IsNullOrEmpty(payload.Text))
            return false;

        try
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(payload.Text);
            package.SetHtmlFormat(HtmlFormatHelper.CreateHtmlFormat(payload.Html));
            Clipboard.SetContent(package);
            Clipboard.Flush();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static MarkdownTableClipboardPayload BuildTableClipboardPayload(
        Layout.Boxes.TableBox table)
    {
        var rows = new string[table.RowCount][];
        for (int row = 0; row < rows.Length; row++)
            rows[row] = new string[table.ColumnCount];

        foreach (var cell in table.GetCellInfos())
            rows[cell.Row][cell.Column] = GetInlineClipboardText(cell.Box);

        var readOnlyRows = new IReadOnlyList<string>[rows.Length];
        for (int row = 0; row < rows.Length; row++)
        {
            for (int column = 0; column < rows[row].Length; column++)
                rows[row][column] ??= string.Empty;
            readOnlyRows[row] = rows[row];
        }
        return MarkdownTableClipboardFormatter.Format(readOnlyRows, table.HeaderRowCount);
    }

    private static string GetInlineClipboardText(Layout.Boxes.InlineContainerBox box)
    {
        var text = new StringBuilder();
        foreach (InlineRun run in box.Runs)
        {
            text.Append(run is InlineImageRun or InlineEmbedRun or InlineVectorSceneRun
                ? run.AccessibleText
                : run.Text);
        }
        return text.ToString();
    }

    private ContextMenuTarget FindContextMenuTarget(Point point)
    {
        var target = new ContextMenuTarget();
        if (_snapshot is null)
            return target;

        foreach (BlockBox block in _snapshot.Blocks)
        {
            if (PopulateContextMenuTarget(block, point, target))
                break;
        }
        return target;
    }

    private static bool PopulateContextMenuTarget(
        BlockBox box,
        Point point,
        ContextMenuTarget target)
    {
        if (!box.Bounds.Contains(point))
            return false;

        switch (box)
        {
            case Layout.Boxes.CodeBlockBox code:
                target.CodeBlock = code;
                return true;
            case Layout.Boxes.ImageBox image:
                target.Image = image;
                target.ImageSourceRange = GetSourceRange(image);
                return true;
            case Layout.Boxes.VectorSceneBox vector:
                if (vector.TryGetLinkAt(point, out var vectorLink) && vectorLink is not null)
                {
                    var semantic = vector.GetSemanticItem(vectorLink.SemanticIndex);
                    target.Link = new LinkTarget(
                        vectorLink.Target ?? string.Empty,
                        semantic?.Description,
                        semantic?.SourceSpan ?? vector.Content.SourceSpan,
                        null,
                        vectorLink.Action,
                        vectorLink.External);
                }
                return true;
            case Layout.Boxes.InlineContainerBox inline:
                PopulateInlineContextMenuTarget(inline, point, target);
                return true;
            case Layout.Boxes.TableBox table:
                target.Table = table;
                foreach (var cell in table.GetCellBoxes())
                {
                    if (PopulateContextMenuTarget(cell, point, target))
                        break;
                }
                return true;
            case Layout.Boxes.ListItemBox listItem:
                if (PopulateContextMenuTarget(listItem.Marker, point, target))
                    return true;
                PopulateContextMenuTarget(listItem.Content, point, target);
                return true;
            case Layout.Boxes.StackBox stack:
                foreach (BlockBox child in stack.Children)
                {
                    if (PopulateContextMenuTarget(child, point, target))
                        return true;
                }
                return true;
            default:
                return true;
        }
    }

    private static void PopulateInlineContextMenuTarget(
        Layout.Boxes.InlineContainerBox inline,
        Point point,
        ContextMenuTarget target)
    {
        InlineRun? run = inline.RunAtVisualPoint(point);
        if (run is LinkRun link)
        {
            target.Link = new LinkTarget(
                link.Url,
                link.Title,
                link.SourceSpan,
                link);
        }
        else if (run is InlineImageRun image)
        {
            target.Image = image.Image;
            target.ImageSourceRange = image.SourceSpan;
            if (image.IsLinked)
            {
                target.Link = new LinkTarget(
                    image.LinkUrl!,
                    image.LinkTitle,
                    image.SourceSpan,
                    null);
            }
        }
    }

    private static SourceSpan GetSourceRange(BlockBox box)
    {
        int start = int.MaxValue;
        int end = 0;
        CollectSourceRange(box, ref start, ref end);
        return start == int.MaxValue || end <= start
            ? SourceSpan.Empty
            : new SourceSpan(start, end - start);
    }

    private static void CollectSourceRange(BlockBox box, ref int start, ref int end)
    {
        switch (box)
        {
            case Layout.Boxes.InlineContainerBox inline:
                foreach (InlineRun run in inline.Runs)
                    IncludeSourceSpan(run.SourceSpan, ref start, ref end);
                return;
            case Layout.Boxes.CodeBlockBox code:
                foreach (var chunk in code.Chunks)
                    CollectSourceRange(chunk, ref start, ref end);
                return;
            case Layout.Boxes.TableBox table:
                foreach (var cell in table.GetCellBoxes())
                    CollectSourceRange(cell, ref start, ref end);
                return;
            case Layout.Boxes.ListItemBox listItem:
                CollectSourceRange(listItem.Marker, ref start, ref end);
                CollectSourceRange(listItem.Content, ref start, ref end);
                return;
            case Layout.Boxes.StackBox stack:
                foreach (BlockBox child in stack.Children)
                    CollectSourceRange(child, ref start, ref end);
                return;
            case Layout.Boxes.VectorSceneBox vector:
                IncludeSourceSpan(vector.Content.SourceSpan, ref start, ref end);
                return;
        }

        if (box is Layout.Boxes.ImageBox image)
        {
            foreach (var entry in image.Context.SourceMap.Entries)
            {
                if (entry.BlockIndex == image.BlockIndex)
                    IncludeSourceSpan(entry.Span, ref start, ref end);
            }
        }
    }

    private static void IncludeSourceSpan(SourceSpan span, ref int start, ref int end)
    {
        if (span.IsEmpty)
            return;
        start = Math.Min(start, span.Start);
        end = Math.Max(end, span.End);
    }

    private Point GetKeyboardContextMenuPoint()
    {
        TryGetViewport(out double viewportTop, out double viewportHeight, out double viewportWidth);
        double viewportBottom = viewportTop + viewportHeight;
        if (_focusableItems is { } items &&
            _focusedItemIndex >= 0 &&
            _focusedItemIndex < items.Count &&
            GetFocusableItemRect(items[_focusedItemIndex]) is { } focusedRect)
        {
            return new Point(
                Math.Clamp(focusedRect.Left + focusedRect.Width / 2.0, 0, Math.Max(0, viewportWidth - 1)),
                Math.Clamp(focusedRect.Bottom, viewportTop, Math.Max(viewportTop, viewportBottom - 1)));
        }

        Rect selectionRect = default;
        foreach (Rect rectangle in _selectionAdornerRects)
        {
            if (rectangle.Bottom < viewportTop || rectangle.Top > viewportBottom)
            {
                continue;
            }

            selectionRect = rectangle;
            break;
        }

        if (selectionRect.Width <= 0 || selectionRect.Height <= 0)
        {
            return new Point(12, viewportTop + 12);
        }

        return new Point(
            Math.Clamp(selectionRect.Left + 8, 0, Math.Max(0, viewportWidth - 1)),
            Math.Clamp(selectionRect.Bottom, viewportTop, Math.Max(viewportTop, viewportBottom - 1)));
    }

    internal static LinkTarget? FindLinkTargetInBlock(BlockBox box, DocumentPosition pos, Point? point)
    {
        switch (box)
        {
            case Layout.Boxes.VectorSceneBox vector when vector.BlockIndex == pos.BlockIndex && point is { } vectorPoint:
                if (vector.TryGetLinkAt(vectorPoint, out var vectorLink) && vectorLink is not null)
                {
                    var semantic = vector.GetSemanticItem(vectorLink.SemanticIndex);
                    return new LinkTarget(
                        vectorLink.Target ?? string.Empty,
                        semantic?.Description,
                        semantic?.SourceSpan ?? vector.Content.SourceSpan,
                        null,
                        vectorLink.Action,
                        vectorLink.External);
                }
                return null;
            case Layout.Boxes.InlineContainerBox icb when icb.BlockIndex == pos.BlockIndex:
                foreach (var r in icb.Runs)
                {
                    if (r.InlineIndex != pos.InlineIndex)
                        continue;

                    if (point is { } p && !icb.IsPointInsideRunBounds(r, p))
                        return null;

                    if (r is LinkRun lr)
                        return new LinkTarget(lr.Url, lr.Title, lr.SourceSpan, lr);

                    if (r is InlineImageRun { IsLinked: true } image)
                        return new LinkTarget(image.LinkUrl!, image.LinkTitle, image.SourceSpan, null);
                }
                return null;
            case Layout.Boxes.ListItemBox lib:
                if (FindLinkTargetInBlock(lib.Marker, pos, point) is { } lm) return lm;
                return FindLinkTargetInBlock(lib.Content, pos, point);
            case Layout.Boxes.TableBox tb:
                foreach (var cell in tb.GetCellBoxes())
                {
                    if (FindLinkTargetInBlock(cell, pos, point) is { } tf) return tf;
                }
                return null;
            case Layout.Boxes.StackBox sb:
                foreach (var c in sb.Children)
                {
                    if (FindLinkTargetInBlock(c, pos, point) is { } f) return f;
                }
                return null;
        }
        return null;
    }

    private static bool IsLinkedRun(InlineRun? run)
        => run is LinkRun or InlineImageRun { IsLinked: true };

    private static MarkdownLinkInputKind GetLinkInputKind(Microsoft.UI.Input.PointerDeviceType deviceType)
        => deviceType switch
        {
            Microsoft.UI.Input.PointerDeviceType.Touch => MarkdownLinkInputKind.Touch,
            Microsoft.UI.Input.PointerDeviceType.Pen => MarkdownLinkInputKind.Pen,
            _ => MarkdownLinkInputKind.Mouse,
        };

    private static MarkdownInputModifiers GetCurrentInputModifiers()
    {
        MarkdownInputModifiers result = MarkdownInputModifiers.None;
        if (IsVirtualKeyDown(VirtualKey.Control)) result |= MarkdownInputModifiers.Control;
        if (IsVirtualKeyDown(VirtualKey.Shift)) result |= MarkdownInputModifiers.Shift;
        if (IsVirtualKeyDown(VirtualKey.Menu)) result |= MarkdownInputModifiers.Alt;
        if (IsVirtualKeyDown(VirtualKey.LeftWindows) || IsVirtualKeyDown(VirtualKey.RightWindows))
            result |= MarkdownInputModifiers.Windows;
        return result;
    }

    private static bool IsVirtualKeyDown(VirtualKey key)
        => (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key) &
            Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;

    private static MarkdownLinkDisposition GetRequestedDisposition(MarkdownInputModifiers modifiers)
    {
        if ((modifiers & MarkdownInputModifiers.Alt) != 0)
            return MarkdownLinkDisposition.External;
        if ((modifiers & MarkdownInputModifiers.Control) != 0 &&
            (modifiers & MarkdownInputModifiers.Shift) == 0)
        {
            return MarkdownLinkDisposition.BackgroundView;
        }
        if ((modifiers & MarkdownInputModifiers.Shift) != 0)
            return MarkdownLinkDisposition.NewView;
        return MarkdownLinkDisposition.CurrentView;
    }

    private bool TryActivateDisclosure(LinkRun? run)
    {
        if (run is not { IsDisclosure: true })
        {
            return false;
        }

        SetDisclosureExpanded(run, !run.IsExpanded);
        return true;
    }

    private readonly record struct LazyLayoutWorkResult(
        LazyLayoutCommit Commit,
        (int BlockIndex, double OffsetFromTop)? ScrollAnchor);

    private sealed class ContextMenuTarget
    {
        public LinkTarget? Link { get; set; }
        public Layout.Boxes.ImageBox? Image { get; set; }
        public SourceSpan ImageSourceRange { get; set; }
        public Layout.Boxes.CodeBlockBox? CodeBlock { get; set; }
        public Layout.Boxes.TableBox? Table { get; set; }
    }

    internal readonly record struct LinkTarget(
        string Url,
        string? Title,
        SourceSpan SourceSpan,
        LinkRun? Run,
        string? Action = null,
        bool External = false);
}

/// <summary>Event data for markdown link activation.</summary>
public sealed class MarkdownLinkClickEventArgs : EventArgs
{
    /// <summary>Initializes link activation event data.</summary>
    public MarkdownLinkClickEventArgs(string url, string? title)
        : this(
            url,
            title,
            SourceSpan.Empty,
            MarkdownLinkInputKind.Programmatic,
            MarkdownInputModifiers.None,
            MarkdownLinkDisposition.CurrentView)
    {
    }

    /// <summary>Initializes complete link activation event data.</summary>
    public MarkdownLinkClickEventArgs(
        string url,
        string? title,
        SourceSpan sourceRange,
        MarkdownLinkInputKind inputKind,
        MarkdownInputModifiers modifiers,
        MarkdownLinkDisposition requestedDisposition,
        string? action = null)
    {
        Url = url ?? string.Empty;
        Title = title;
        SourceRange = sourceRange;
        InputKind = inputKind;
        Modifiers = modifiers;
        RequestedDisposition = requestedDisposition;
        Action = string.IsNullOrWhiteSpace(action) ? null : action;
        if (Uri.TryCreate(Url, UriKind.RelativeOrAbsolute, out var uri))
            Uri = uri;
    }
    /// <summary>Gets the link URL.</summary>
    public string Url { get; }

    /// <summary>Gets the optional link title.</summary>
    public string? Title { get; }

    /// <summary>Gets the parsed relative or absolute URI, when valid.</summary>
    public Uri? Uri { get; }

    /// <summary>Gets the declarative host action identifier, when present.</summary>
    public string? Action { get; }

    /// <summary>Gets the half-open UTF-16 source range of the activated link.</summary>
    public SourceSpan SourceRange { get; }

    /// <summary>Gets the input modality that activated the link.</summary>
    public MarkdownLinkInputKind InputKind { get; }

    /// <summary>Gets keyboard modifiers active for the activation.</summary>
    public MarkdownInputModifiers Modifiers { get; }

    /// <summary>Gets the navigation disposition requested by the gesture.</summary>
    public MarkdownLinkDisposition RequestedDisposition { get; }

    /// <summary>Gets or sets whether the host handled the activation.</summary>
    public bool Handled { get; set; }
}

/// <summary>Identifies the input modality used to activate markdown content.</summary>
public enum MarkdownLinkInputKind
{
    /// <summary>The activation was raised by application code.</summary>
    Programmatic,
    /// <summary>Mouse input.</summary>
    Mouse,
    /// <summary>Touch input.</summary>
    Touch,
    /// <summary>Pen input.</summary>
    Pen,
    /// <summary>Keyboard input.</summary>
    Keyboard,
    /// <summary>A UI Automation client invoked the element.</summary>
    Automation,
}

/// <summary>Keyboard modifiers captured for a markdown command or activation.</summary>
[Flags]
public enum MarkdownInputModifiers
{
    /// <summary>No modifier keys.</summary>
    None = 0,
    /// <summary>Control key.</summary>
    Control = 1,
    /// <summary>Shift key.</summary>
    Shift = 2,
    /// <summary>Alt/Menu key.</summary>
    Alt = 4,
    /// <summary>Windows key.</summary>
    Windows = 8,
}

/// <summary>Navigation disposition requested by a link gesture.</summary>
public enum MarkdownLinkDisposition
{
    /// <summary>Navigate in the host's current view.</summary>
    CurrentView,
    /// <summary>Open a new foreground view or window.</summary>
    NewView,
    /// <summary>Open a new background view when the host supports it.</summary>
    BackgroundView,
    /// <summary>Delegate navigation to the system or external application.</summary>
    External,
}

/// <summary>Event data for safe HTML disclosure activation.</summary>
public sealed class MarkdownDisclosureToggledEventArgs : EventArgs
{
    /// <summary>Initializes disclosure event data.</summary>
    public MarkdownDisclosureToggledEventArgs(bool isExpanded) => IsExpanded = isExpanded;

    /// <summary>Gets whether the disclosure is expanded after activation.</summary>
    public bool IsExpanded { get; }
}

/// <summary>Identifies the Markdown content copied to the clipboard.</summary>
public enum MarkdownCopyKind
{
    /// <summary>The active text selection.</summary>
    Selection,

    /// <summary>An entire fenced or indented code block.</summary>
    CodeBlock,
}

/// <summary>Event data for a Markdown clipboard copy attempt.</summary>
public sealed class MarkdownCopyCompletedEventArgs : EventArgs
{
    /// <summary>Initializes clipboard copy event data.</summary>
    public MarkdownCopyCompletedEventArgs(MarkdownCopyKind kind, bool succeeded)
    {
        Kind = kind;
        Succeeded = succeeded;
    }

    /// <summary>Gets the copied content kind.</summary>
    public MarkdownCopyKind Kind { get; }

    /// <summary>Gets whether the clipboard accepted the content.</summary>
    public bool Succeeded { get; }
}

/// <summary>Event data for a Markdown document render failure.</summary>
public sealed class MarkdownRenderFailedEventArgs : EventArgs
{
    /// <summary>Initializes render-failure event data.</summary>
    public MarkdownRenderFailedEventArgs(Exception exception)
    {
        Exception = exception ?? throw new ArgumentNullException(nameof(exception));
    }

    /// <summary>Gets the exception raised by the render pipeline.</summary>
    public Exception Exception { get; }
}


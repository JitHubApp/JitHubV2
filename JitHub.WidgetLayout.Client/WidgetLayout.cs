namespace JitHub.WidgetLayout.Client
{
    using Microsoft.UI.Xaml.Controls;
    using System;
    using System.Collections.Generic;
    using System.Collections.Specialized;
    using System.Diagnostics;
    using Windows.Foundation;
    using Windows.UI.Composition;
    using Windows.UI.Xaml;
    using Windows.UI.Xaml.Hosting;

    public partial class WidgetLayout : VirtualizingLayout
    {
        /// <summary>
        /// Gets or sets the Columns
        /// </summary>
        public int Columns
        {
            get
            {
                return (int)GetValue(ColumnsProperty);
            }

            set
            {
                SetValue(ColumnsProperty, value);
            }
        }

        /// <summary>
        /// Defines the ColumnsProperty
        /// </summary>
        public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
            nameof(Columns),
            typeof(int),
            typeof(WidgetLayout),
            new PropertyMetadata(4, OnLayoutPropertyChanged));

        /// <summary>
        /// Gets or sets the MinColumns
        /// </summary>
        public int MinColumns
        {
            get
            {
                return (int)GetValue(MinColumnsProperty);
            }

            set
            {
                SetValue(MinColumnsProperty, value);
            }
        }

        /// <summary>
        /// Defines the MinColumnsProperty
        /// </summary>
        public static readonly DependencyProperty MinColumnsProperty = DependencyProperty.Register(
            nameof(MinColumns),
            typeof(int),
            typeof(WidgetLayout),
            new PropertyMetadata(1, OnLayoutPropertyChanged));

        /// <summary>
        /// Gets or sets the ColumnWidth
        /// </summary>
        public double ColumnWidth
        {
            get
            {
                return (double)GetValue(ColumnWidthProperty);
            }

            set
            {
                SetValue(ColumnWidthProperty, value);
            }
        }

        /// <summary>
        /// Defines the ColumnWidthProperty
        /// </summary>
        public static readonly DependencyProperty ColumnWidthProperty = DependencyProperty.Register(
            nameof(ColumnWidth),
            typeof(double),
            typeof(WidgetLayout),
            new PropertyMetadata(140d, OnLayoutPropertyChanged));

        /// <summary>
        /// Gets or sets the RowHeight
        /// </summary>
        public double RowHeight
        {
            get
            {
                return (double)GetValue(RowHeightProperty);
            }

            set
            {
                SetValue(RowHeightProperty, value);
            }
        }

        /// <summary>
        /// Defines the RowHeightProperty
        /// </summary>
        public static readonly DependencyProperty RowHeightProperty = DependencyProperty.Register(
            nameof(RowHeight),
            typeof(double),
            typeof(WidgetLayout),
            new PropertyMetadata(140d, OnLayoutPropertyChanged));

        /// <summary>
        /// Gets or sets the Spacing
        /// </summary>
        public double Spacing
        {
            get
            {
                return (double)GetValue(SpacingProperty);
            }

            set
            {
                SetValue(SpacingProperty, value);
            }
        }

        /// <summary>
        /// Defines the SpacingProperty
        /// </summary>
        public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
            nameof(Spacing),
            typeof(double),
            typeof(WidgetLayout),
            new PropertyMetadata(12d, OnLayoutPropertyChanged));

        /// <summary>
        /// Gets or sets the HorizontalMode
        /// </summary>
        public HorizontalLayoutMode HorizontalMode
        {
            get
            {
                return (HorizontalLayoutMode)GetValue(HorizontalModeProperty);
            }

            set
            {
                SetValue(HorizontalModeProperty, value);
            }
        }

        /// <summary>
        /// Defines the HorizontalModeProperty
        /// </summary>
        public static readonly DependencyProperty HorizontalModeProperty = DependencyProperty.Register(
            nameof(HorizontalMode),
            typeof(HorizontalLayoutMode),
            typeof(WidgetLayout),
            new PropertyMetadata(HorizontalLayoutMode.Center, OnLayoutPropertyChanged));

        /// <summary>
        /// Gets or sets the ReflowAnimationDuration
        /// </summary>
        public double ReflowAnimationDuration
        {
            get
            {
                return (double)GetValue(ReflowAnimationDurationProperty);
            }

            set
            {
                SetValue(ReflowAnimationDurationProperty, value);
            }
        }

        /// <summary>
        /// Defines the ReflowAnimationDurationProperty
        /// </summary>
        public static readonly DependencyProperty ReflowAnimationDurationProperty = DependencyProperty.Register(
            nameof(ReflowAnimationDuration),
            typeof(double),
            typeof(WidgetLayout),
            new PropertyMetadata(350d));

        /// <summary>
        /// Gets or sets the StandardAnimationDuration
        /// </summary>
        public double StandardAnimationDuration
        {
            get
            {
                return (double)GetValue(StandardAnimationDurationProperty);
            }

            set
            {
                SetValue(StandardAnimationDurationProperty, value);
            }
        }

        /// <summary>
        /// Defines the StandardAnimationDurationProperty
        /// </summary>
        public static readonly DependencyProperty StandardAnimationDurationProperty = DependencyProperty.Register(
            nameof(StandardAnimationDuration),
            typeof(double),
            typeof(WidgetLayout),
            new PropertyMetadata(220d));

        /// <summary>
        /// Gets or sets a value indicating whether EnableDiagnostics
        /// </summary>
        public bool EnableDiagnostics
        {
            get
            {
                return (bool)GetValue(EnableDiagnosticsProperty);
            }

            set
            {
                SetValue(EnableDiagnosticsProperty, value);
            }
        }

        /// <summary>
        /// Defines the EnableDiagnosticsProperty
        /// </summary>
        public static readonly DependencyProperty EnableDiagnosticsProperty = DependencyProperty.Register(
            nameof(EnableDiagnostics),
            typeof(bool),
            typeof(WidgetLayout),
            new PropertyMetadata(false));

        /// <summary>
        /// Gets or sets the MaxMeasureRealizationCount
        /// </summary>
        public int MaxMeasureRealizationCount
        {
            get
            {
                return (int)GetValue(MaxMeasureRealizationCountProperty);
            }

            set
            {
                SetValue(MaxMeasureRealizationCountProperty, value);
            }
        }

        /// <summary>
        /// Defines the MaxMeasureRealizationCountProperty
        /// </summary>
        public static readonly DependencyProperty MaxMeasureRealizationCountProperty = DependencyProperty.Register(
            nameof(MaxMeasureRealizationCount),
            typeof(int),
            typeof(WidgetLayout),
            new PropertyMetadata(200));

        /// <summary>
        /// Gets or sets the DragHoverCommitDelay
        /// </summary>
        public double DragHoverCommitDelay
        {
            get
            {
                return (double)GetValue(DragHoverCommitDelayProperty);
            }

            set
            {
                SetValue(DragHoverCommitDelayProperty, value);
            }
        }

        /// <summary>
        /// Defines the DragHoverCommitDelayProperty
        /// </summary>
        public static readonly DependencyProperty DragHoverCommitDelayProperty = DependencyProperty.Register(
            nameof(DragHoverCommitDelay), typeof(double), typeof(WidgetLayout), new PropertyMetadata(200d));


        /// <summary>
        /// Gets or sets the DragActivationDistance
        /// </summary>
        public double DragActivationDistance
        {
            get
            {
                return (double)GetValue(DragActivationDistanceProperty);
            }

            set
            {
                SetValue(DragActivationDistanceProperty, value);
            }
        }

        /// <summary>
        /// Defines the DragActivationDistanceProperty
        /// </summary>
        public static readonly DependencyProperty DragActivationDistanceProperty = DependencyProperty.Register(
            nameof(DragActivationDistance), typeof(double), typeof(WidgetLayout), new PropertyMetadata(4d));


        /// <summary>
        /// The GetColumnSpan
        /// </summary>
        /// <param name="obj">The obj<see cref="DependencyObject"/></param>
        /// <returns>The <see cref="int"/></returns>
        public static int GetColumnSpan(DependencyObject obj)
        {
            return (int)obj.GetValue(ColumnSpanProperty);
        }

        /// <summary>
        /// The SetColumnSpan
        /// </summary>
        /// <param name="obj">The obj<see cref="DependencyObject"/></param>
        /// <param name="value">The value<see cref="int"/></param>
        public static void SetColumnSpan(DependencyObject obj, int value)
        {
            obj.SetValue(ColumnSpanProperty, value);
        }

        /// <summary>
        /// Defines the ColumnSpanProperty
        /// </summary>
        public static readonly DependencyProperty ColumnSpanProperty = DependencyProperty.RegisterAttached(
            "ColumnSpan",
            typeof(int),
            typeof(WidgetLayout),
            new PropertyMetadata(1, OnLayoutPropertyChanged));

        /// <summary>
        /// The GetRowSpan
        /// </summary>
        /// <param name="obj">The obj<see cref="DependencyObject"/></param>
        /// <returns>The <see cref="int"/></returns>
        public static int GetRowSpan(DependencyObject obj)
        {
            return (int)obj.GetValue(RowSpanProperty);
        }

        /// <summary>
        /// The SetRowSpan
        /// </summary>
        /// <param name="obj">The obj<see cref="DependencyObject"/></param>
        /// <param name="value">The value<see cref="int"/></param>
        public static void SetRowSpan(DependencyObject obj, int value)
        {
            obj.SetValue(RowSpanProperty, value);
        }

        /// <summary>
        /// Defines the RowSpanProperty
        /// </summary>
        public static readonly DependencyProperty RowSpanProperty = DependencyProperty.RegisterAttached(
            "RowSpan",
            typeof(int),
            typeof(WidgetLayout),
            new PropertyMetadata(1, OnLayoutPropertyChanged));

        /// <summary>
        /// Gets the DraggedIndex
        /// </summary>
        public int DraggedIndex { get; private set; } = -1;

        /// <summary>
        /// Gets the DragTargetIndex
        /// </summary>
        public int DragTargetIndex { get; private set; } = -1;

        /// <summary>
        /// The BeginDrag
        /// </summary>
        /// <param name="index">The index<see cref="int"/></param>
        public void BeginDrag(int index)
        {
            _isDragging = true;
            DraggedIndex = index;
            DragTargetIndex = index; // Keep original target until activation / commit
            _originalDragTargetIndex = index; // track original
            _dragPointer = new Point(-1, -1);
            _dragStartPointer = new Point(-1, -1);
            _dragMovementActivated = false;
            _needsHoverReset = true;
            _reorderSuppressed = true; // suppress mapping until a hover commit occurs
            _hoverCommitted = false; // reset commit state
            _dragCommittedThisCycle = false;
            // Capture a snapshot of current layout rects immediately (if available) so we can revert on cancel
            if (_layoutStateRef != null && _layoutStateRef.IndexToRect.Count > 0)
            {
                _preDragRects = new Dictionary<int, Rect>(_layoutStateRef.IndexToRect);
                if (EnableDiagnostics)
                {
                    Debug.WriteLine($"[WidgetLayout] BeginDrag captured snapshot count={_preDragRects.Count}");
                }
            }
            else
            {
                _preDragRects = null; // will capture later on first arrange if not present yet
            }
            InvalidateArrange();
        }

        /// <summary>
        /// The UpdateDragTarget
        /// </summary>
        /// <param name="targetIndex">The targetIndex<see cref="int"/></param>
        public void UpdateDragTarget(int targetIndex)
        {
            if (DraggedIndex < 0)
            {
                return;
            }

            // Ignore external targets during active drag unless explicitly enabled
            if (!UseExternalTargetUpdatesDuringDrag && _isDragging)
            {
                return;
            }

            // Prevent premature reordering: ignore external target updates until drag movement activated
            if (!_dragMovementActivated)
            {
                if (EnableDiagnostics)
                {
                    Debug.WriteLine($"[WidgetLayout] Ignoring UpdateDragTarget({targetIndex}) before activation threshold reached.");
                }
                return;
            }

            targetIndex = Math.Max(0, targetIndex);
            if (targetIndex == DragTargetIndex)
            {
                return;
            }

            DragTargetIndex = targetIndex;
            InvalidateArrange();
        }

        /// <summary>
        /// The UpdateDragPointer
        /// </summary>
        /// <param name="p">The p<see cref="Point"/></param>
        public void UpdateDragPointer(Point p)
        {
            if (!_isDragging)
            {
                return;
            }

            if (_dragStartPointer.X < 0 && _dragStartPointer.Y < 0)
            {
                _dragStartPointer = p;
            }
            _dragPointer = p;
            if (!_dragMovementActivated)
            {
                var dx = _dragPointer.X - _dragStartPointer.X;
                var dy = _dragPointer.Y - _dragStartPointer.Y;
                if (Math.Abs(dx) >= DragActivationDistance || Math.Abs(dy) >= DragActivationDistance)
                {
                    _dragMovementActivated = true; // do NOT unsuppress reorder yet; wait for hover commit
                }
            }
            InvalidateArrange();
        }

        /// <summary>
        /// The CompleteDrag
        /// </summary>
        /// <param name="commitMove">The commitMove<see cref="Action{int,int}"/></param>
        public void CompleteDrag(Action<int, int> commitMove)
        {
            bool committed = false;
            int originalDragged = DraggedIndex;
            int originalTarget = DragTargetIndex;
            bool couldCommit = !_reorderSuppressed && _dragMovementActivated && _hoverCommitted && DraggedIndex >= 0;

            // If user reverted hover (DragTargetIndex == DraggedIndex) but previously hovered a different spot, use that
            if (couldCommit && DragTargetIndex == DraggedIndex && _lastHoverCommittedTarget >= 0 && _lastHoverCommittedTarget != DraggedIndex)
            {
                DragTargetIndex = _lastHoverCommittedTarget;
                if (EnableDiagnostics)
                {
                    Debug.WriteLine($"[WidgetLayout] CompleteDrag using lastHoverCommittedTarget={_lastHoverCommittedTarget}");
                }
            }

            // If we still have a pending hover candidate adopt it before committing (only if we already allowed reorder)
            if (_isDragging && !_reorderSuppressed && _dragMovementActivated && _layoutStateRef is LayoutState ls)
            {
                if (ls.HoverCandidateIndex >= 0 && ls.HoverCandidateIndex != DragTargetIndex)
                {
                    DragTargetIndex = ls.HoverCandidateIndex;
                }
            }
            // Adopt pending hover candidate if timer hasn't fired yet but intent is clear
            if (_isDragging && _dragMovementActivated && _layoutStateRef is LayoutState ls2 && _reorderSuppressed)
            {
                if (ls2.HoverCandidateIndex >= 0 && ls2.HoverCandidateIndex != DragTargetIndex)
                {
                    DragTargetIndex = ls2.HoverCandidateIndex;
                    _reorderSuppressed = false; // allow commit
                }
            }
            // If no commit occurred (still suppressed) treat as no-op
            if (_reorderSuppressed || !_dragMovementActivated || !_hoverCommitted)
            {
                DragTargetIndex = DraggedIndex;
            }
            if (DraggedIndex >= 0 && DragTargetIndex >= 0 && DraggedIndex != DragTargetIndex)
            {
                try
                {
                    commitMove?.Invoke(DraggedIndex, DragTargetIndex);
                    committed = true;
                    _dragCommittedThisCycle = true;
                    _forceRemapAfterDrag = true; // ensure we rebuild layout mapping next arrange
                    _lastCommitFrom = DraggedIndex;
                    _lastCommitTo = DragTargetIndex;
                    _pendingPostCommitFix = true;
                    _pendingCommitFinalize = true; // trigger finalize pass
                    _pendingCommitFrom = DraggedIndex;
                    _pendingCommitTo = DragTargetIndex;
                }
                catch (Exception ex)
                {
                    if (EnableDiagnostics)
                    {
                        Debug.WriteLine($"[WidgetLayout] commitMove exception: {ex.Message}");
                    }
                }
            }
            if (EnableDiagnostics)
            {
                Debug.WriteLine($"[WidgetLayout] CompleteDrag Dragged={originalDragged} FinalTarget={DragTargetIndex} Committed={committed} ReorderSuppressed={_reorderSuppressed} Activated={_dragMovementActivated} HoverCommitted={_hoverCommitted}");
            }
            CancelDrag();
            if (committed)
            {
                if (EnableDiagnostics)
                {
                    Debug.WriteLine($"[WidgetLayout] CompleteDrag committed -> scheduling remap; Dragged={originalDragged} -> {DragTargetIndex}");
                }
                InvalidateMeasure();
            }
            else
            {
                InvalidateArrange();
            }
        }

        /// <summary>
        /// The CancelDrag
        /// </summary>
        public void CancelDrag()
        {
            // If we will need the snapshot for cancel, keep a local reference before nulling
            bool hadSnapshot = _preDragRects != null;
            var snapshot = _preDragRects; // local copy

            _isDragging = false;
            DraggedIndex = -1;
            DragTargetIndex = -1;
            _dragPointer = new Point(-1, -1);
            _dragStartPointer = new Point(-1, -1);
            _dragMovementActivated = false;
            _needsHoverReset = true;
            _hoverCommitted = false;
            _reorderSuppressed = false; // reset
            _lastHoverCommittedTarget = -1;

            // Do NOT null _preDragRects until after potential restore

            if (!_dragCommittedThisCycle && hadSnapshot && snapshot != null)
            {
                if (EnableDiagnostics)
                {
                    Debug.WriteLine($"[WidgetLayout] CancelDrag restoring pre-drag rect snapshot count={snapshot.Count}");
                }
                if (_layoutStateRef is LayoutState lsRestore)
                {
                    lsRestore.IndexToRect.Clear();
                    foreach (var kv in snapshot)
                    {
                        lsRestore.IndexToRect[kv.Key] = kv.Value;
                    }
                }
            }

            _preDragRects = null; // release snapshot finally
            InvalidateArrange();
        }

        /// <summary>
        /// Gets the HorizontalOffset
        /// </summary>
        public double HorizontalOffset => _lastHorizontalOffset;

        /// <summary>
        /// The OnLayoutPropertyChanged
        /// </summary>
        /// <param name="d">The d<see cref="DependencyObject"/></param>
        /// <param name="e">The e<see cref="DependencyPropertyChangedEventArgs"/></param>
        private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            (d as WidgetLayout)?.InvalidateMeasure();
        }

        /// <summary>
        /// Defines the <see cref="LayoutState" />
        /// </summary>
        private class LayoutState
        {
            /// <summary>
            /// Defines the IndexToRect
            /// </summary>
            public Dictionary<int, Rect> IndexToRect = new Dictionary<int, Rect>();

            /// <summary>
            /// Defines the ImplicitAnimations
            /// </summary>
            public ImplicitAnimationCollection ImplicitAnimations;

            /// <summary>
            /// Defines the AnimationAssigned
            /// </summary>
            public HashSet<UIElement> AnimationAssigned = new HashSet<UIElement>();

            /// <summary>
            /// Defines the Measuring
            /// </summary>
            public bool Measuring;

            /// <summary>
            /// Defines the Arranging
            /// </summary>
            public bool Arranging;

            /// <summary>
            /// Defines the ArrangePassesSinceLastMeasure
            /// </summary>
            public int ArrangePassesSinceLastMeasure;

            /// <summary>
            /// Defines the TotalWidth
            /// </summary>
            public double TotalWidth;

            /// <summary>
            /// Defines the TotalHeight
            /// </summary>
            public double TotalHeight;

            /// <summary>
            /// Defines the HorizontalOffset
            /// </summary>
            public double HorizontalOffset;

            /// <summary>
            /// Defines the EffectiveColumns
            /// </summary>
            public int EffectiveColumns;

            /// <summary>
            /// Defines the PreviousColumns
            /// </summary>
            public int PreviousColumns = -1;

            /// <summary>
            /// Defines the ColumnsChanged
            /// </summary>
            public bool ColumnsChanged;

            // Hover commit delay state
            public int HoverCandidateIndex = -1;
            public DispatcherTimer HoverTimer;
        }

        /// <summary>
        /// Defines the _isDragging
        /// </summary>
        private bool _isDragging;

        /// <summary>
        /// Defines the ArrangePassGuardThreshold
        /// </summary>
        private const int ArrangePassGuardThreshold = 300;

        /// <summary>
        /// Defines the _dragPointer
        /// </summary>
        private Point _dragPointer = new Point(-1, -1);

        /// <summary>
        /// Defines the _dragStartPointer
        /// </summary>
        private Point _dragStartPointer = new Point(-1, -1);

        /// <summary>
        /// Defines the _dragMovementActivated
        /// </summary>
        private bool _dragMovementActivated; // has user moved past activation threshold

        /// <summary>
        /// Defines the _needsHoverReset
        /// </summary>
        private bool _needsHoverReset; // flag to reset hover timers next arrange

        /// <summary>
        /// Defines the _lastHorizontalOffset
        /// </summary>
        private double _lastHorizontalOffset;

        /// <summary>
        /// Defines the _reorderSuppressed
        /// </summary>
        private bool _reorderSuppressed; // stops mapping before activation
        private int _originalDragTargetIndex = -1;
        private LayoutState _layoutStateRef; // reference to current layout state for CompleteDrag
        private bool _hoverCommitted; // indicates at least one hover-based commit occurred during drag
        // Diagnostics throttling
        private double _lastLoggedAvailWidth = -1;
        private double _lastLoggedOffset = double.NaN;
        private int _lastLoggedItemCount = -1;
        private int _lastLoggedDragIndex = -1;
        private int _lastLoggedTargetIndex = -1;
        private Windows.Foundation.Rect _lastLoggedPlaceholderRect = Windows.Foundation.Rect.Empty;
        private Dictionary<int, Rect> _preDragRects; // snapshot of layout at drag start for immediate revert
        private int _lastLoggedHoverCandidate = -1;
        private bool _forceRemapAfterDrag; // triggers a fresh measure right after a committed reorder

        // Post-commit correction state
        private bool _pendingPostCommitFix;
        private int _lastCommitFrom = -1;
        private int _lastCommitTo = -1;
        // Commit finalize animation tracking
        private bool _pendingCommitFinalize;
        private int _pendingCommitFrom = -1;
        private int _pendingCommitTo = -1;
        // Track whether last drag resulted in a committed reorder
        private bool _dragCommittedThisCycle;
        // Track last successful non-revert hover commit target (so we can still commit even if target visually reverts)
        private int _lastHoverCommittedTarget = -1;

        /// <summary>
        /// The InitializeForContextCore
        /// </summary>
        /// <param name="context">The context<see cref="VirtualizingLayoutContext"/></param>
        protected override void InitializeForContextCore(VirtualizingLayoutContext context)
        {
            if (!(context.LayoutState is LayoutState))
            {
                context.LayoutState = new LayoutState();
            }
        }

        /// <summary>
        /// The UninitializeForContextCore
        /// </summary>
        /// <param name="context">The context<see cref="VirtualizingLayoutContext"/></param>
        protected override void UninitializeForContextCore(VirtualizingLayoutContext context)
        {
            if (context.LayoutState is LayoutState ls)
            {
                ls.IndexToRect.Clear();
                ls.ImplicitAnimations = null;
            }
        }

        /// <summary>
        /// The OnItemsChangedCore
        /// </summary>
        /// <param name="context">The context<see cref="VirtualizingLayoutContext"/></param>
        /// <param name="source">The source<see cref="object"/></param>
        /// <param name="args">The args<see cref="NotifyCollectionChangedEventArgs"/></param>
        protected override void OnItemsChangedCore(VirtualizingLayoutContext context, object source, NotifyCollectionChangedEventArgs args)
        {
            if (context.LayoutState is LayoutState ls)
            {
                ls.IndexToRect.Clear();
            }
            InvalidateMeasure();
        }

        /// <summary>
        /// The MeasureOverride
        /// </summary>
        /// <param name="context">The context<see cref="VirtualizingLayoutContext"/></param>
        /// <param name="availableSize">The availableSize<see cref="Size"/></param>
        /// <returns>The <see cref="Size"/></returns>
        protected override Size MeasureOverride(VirtualizingLayoutContext context, Size availableSize)
        {
            var state = (LayoutState)context.LayoutState;
            if (state.Measuring)
            {
                return availableSize;
            }

            state.Measuring = true;
            try
            {
                state.IndexToRect.Clear();
                state.ArrangePassesSinceLastMeasure = 0;
                int maxColumns = Math.Max(1, Columns);
                int minColumns = Math.Max(1, MinColumns);
                if (minColumns > maxColumns)
                {
                    minColumns = maxColumns;
                }

                double colW = ColumnWidth;
                double rowH = RowHeight;
                double spacing = Spacing;
                int count = context.ItemCount;
                bool finiteWidth = !double.IsInfinity(availableSize.Width) && availableSize.Width > 0;
                int columns = maxColumns;
                if (finiteWidth)
                {
                    int capacity = (int)Math.Floor((availableSize.Width + spacing) / (colW + spacing));
                    columns = Math.Max(minColumns, Math.Min(maxColumns, capacity));
                }
                state.EffectiveColumns = columns;
                state.ColumnsChanged = state.PreviousColumns != -1 && state.PreviousColumns != columns;
                state.PreviousColumns = columns;
                var occupied = new HashSet<(int c, int r)>();
                int maxRow = -1;
                double usedWidth = (columns * colW) + ((columns - 1) * spacing);
                double horizontalOffset = 0;
                if (finiteWidth && availableSize.Width > usedWidth)
                {
                    double leftover = availableSize.Width - usedWidth;
                    switch (HorizontalMode)
                    {
                        case HorizontalLayoutMode.Left:
                            horizontalOffset = 0;
                            break;
                        case HorizontalLayoutMode.Right:
                            horizontalOffset = leftover;
                            break;
                        case HorizontalLayoutMode.Center:
                        default:
                            horizontalOffset = leftover / 2.0;
                            break;
                    }
                }
                state.HorizontalOffset = horizontalOffset;
                _lastHorizontalOffset = horizontalOffset;
                for (int i = 0; i < count; i++)
                {
                    FrameworkElement element = null;
                    bool realize = i < MaxMeasureRealizationCount;
                    if (realize)
                    {
                        try
                        {
                            element = context.GetOrCreateElementAt(i) as FrameworkElement;
                        }
                        catch (Exception ex) { if (EnableDiagnostics) { Debug.WriteLine("[WidgetLayout] GetOrCreateElementAt exception: " + ex.Message); } }
                    }
                    int colSpan = 1, rowSpan = 1;
                    if (element != null)
                    {
                        colSpan = Math.Min(Math.Max(1, GetColumnSpan(element)), columns);
                        rowSpan = Math.Max(1, GetRowSpan(element));
                    }
                    int placedCol = 0, placedRow = 0;
                    bool found = false;
                    for (int r = 0; !found; r++)
                    {
                        for (int c = 0; c < columns; c++)
                        {
                            if (CanPlace(c, r, colSpan, rowSpan, occupied, columns))
                            {
                                placedCol = c;
                                placedRow = r;
                                found = true;
                                break;
                            }
                        }
                    }
                    for (int r = placedRow; r < placedRow + rowSpan; r++)
                    {
                        for (int c = placedCol; c < placedCol + colSpan; c++)
                        {
                            occupied.Add((c, r));
                        }
                    }
                    maxRow = Math.Max(maxRow, placedRow + rowSpan - 1);
                    double width = (colSpan * colW) + ((colSpan - 1) * spacing);
                    double height = (rowSpan * rowH) + ((rowSpan - 1) * spacing);
                    if (element != null)
                    {
                        element.Measure(new Size(width, height));
                    }

                    state.IndexToRect[i] = new Rect(horizontalOffset + (placedCol * (colW + spacing)), placedRow * (rowH + spacing), width, height);
                }
                double totalHeight = ((maxRow + 1) * rowH) + (Math.Max(0, maxRow) * spacing);
                state.TotalWidth = usedWidth;
                state.TotalHeight = totalHeight;

                // Diagnostics: verify index map completeness
                if (EnableDiagnostics)
                {
                    int missing = 0;
                    for (int idxCheck = 0; idxCheck < count; idxCheck++)
                    {
                        if (!state.IndexToRect.ContainsKey(idxCheck))
                        {
                            missing++;
                        }
                    }
                    if (missing > 0)
                    {
                        Debug.WriteLine($"[WidgetLayout] MeasureIntegrity MissingRects={missing} FromCount={count}");
                    }
                }
                if (EnableDiagnostics)
                {
                    bool log = false;
                    if (_lastLoggedAvailWidth != availableSize.Width || _lastLoggedOffset != horizontalOffset || _lastLoggedItemCount != count)
                    {
                        log = true;
                        _lastLoggedAvailWidth = availableSize.Width;
                        _lastLoggedOffset = horizontalOffset;
                        _lastLoggedItemCount = count;
                    }
                    if (log)
                    {
                        Debug.WriteLine($"[WidgetLayout] Measure Summary Items={count} AvailW={availableSize.Width:F1} UsedW={usedWidth:F1} Offset={horizontalOffset:F1} Cols={columns} Changed={state.ColumnsChanged}");
                    }
                }

                double reportedWidth = finiteWidth ? availableSize.Width : usedWidth;
                return new Size(reportedWidth, totalHeight);
            }
            finally { state.Measuring = false; }
        }

        /// <summary>
        /// The CanPlace
        /// </summary>
        /// <param name="startCol">The startCol<see cref="int"/></param>
        /// <param name="startRow">The startRow<see cref="int"/></param>
        /// <param name="colSpan">The colSpan<see cref="int"/></param>
        /// <param name="rowSpan">The rowSpan<see cref="int"/></param>
        /// <param name="occ">The occ<see cref="HashSet{(int c,int r)}"/></param>
        /// <param name="columns">The columns<see cref="int"/></param>
        /// <returns>The <see cref="bool"/></returns>
        private static bool CanPlace(int startCol, int startRow, int colSpan, int rowSpan, HashSet<(int c, int r)> occ, int columns)
        {
            if (startCol + colSpan > columns)
            {
                return false;
            }

            for (int r = startRow; r < startRow + rowSpan; r++)
            {
                for (int c = startCol; c < startCol + colSpan; c++)
                {
                    if (occ.Contains((c, r)))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        /// <summary>
        /// The ArrangeOverride
        /// </summary>
        /// <param name="context">The context<see cref="VirtualizingLayoutContext"/></param>
        /// <param name="finalSize">The finalSize<see cref="Size"/></param>
        /// <returns>The <see cref="Size"/></returns>
        protected override Size ArrangeOverride(VirtualizingLayoutContext context, Size finalSize)
        {
            var state = (LayoutState)context.LayoutState;
            _layoutStateRef = state;
            if (state.Arranging)
            {
                return finalSize;
            }

            state.Arranging = true;
            try
            {
                if (_needsHoverReset)
                {
                    StopHoverTimer(state);
                    state.HoverCandidateIndex = -1;
                    _needsHoverReset = false;
                }
                EnsureImplicitAnimations(state, context);
                if (state.ColumnsChanged)
                {
                    RecreateImplicitAnimations(state, context, true);
                    state.ColumnsChanged = false;
                }

                // Early remap after a committed reorder (do this before any drag handling so stale simulated map isn't used)
                if (_forceRemapAfterDrag)
                {
                    if (EnableDiagnostics)
                    {
                        Debug.WriteLine("[WidgetLayout] ForceRemapAfterDrag -> clearing rect map and re-measuring");
                    }
                    _forceRemapAfterDrag = false;
                    // Instead of clearing map (causes jump back), keep current map so we can animate delta.
                    // Just invalidate measure to recompute target rects; keep existing offsets.
                    state.Arranging = false;
                    InvalidateMeasure();
                    return finalSize;
                }

                // If activation occurred earlier in UpdateDragPointer we don't need to re-check here, but keep fallback
                if (_isDragging && _reorderSuppressed && !_dragMovementActivated && _dragPointer.X >= 0 && _dragPointer.Y >= 0 && _dragStartPointer.X >= 0)
                {
                    var dx = _dragPointer.X - _dragStartPointer.X;
                    var dy = _dragPointer.Y - _dragStartPointer.Y;
                    if (Math.Abs(dx) >= DragActivationDistance || Math.Abs(dy) >= DragActivationDistance)
                    {
                        _dragMovementActivated = true; /* keep _reorderSuppressed until hover commit */
                    }
                }

                if (_isDragging && _dragMovementActivated /* allow even when suppressed so we can gather hover intent */ && DraggedIndex >= 0 && state.IndexToRect.Count > 0 && _dragPointer.X >= 0 && _dragPointer.Y >= 0)
                {
                    int proposed = DraggedIndex;
                    Rect draggedOriginalRect = Rect.Empty;
                    if (_preDragRects != null && _preDragRects.TryGetValue(DraggedIndex, out var pr))
                    {
                        draggedOriginalRect = pr;
                    }
                    else if (state.IndexToRect.TryGetValue(DraggedIndex, out var cr))
                    {
                        draggedOriginalRect = cr;
                    }
                    // Determine target strictly by pointer inside bounds of another item's rect
                    foreach (var kv in state.IndexToRect)
                    {
                        int idx = kv.Key;
                        if (idx == DraggedIndex)
                        {
                            continue;
                        }
                        var rect = kv.Value;
                        if (_dragPointer.X >= rect.X && _dragPointer.X <= rect.Right && _dragPointer.Y >= rect.Y && _dragPointer.Y <= rect.Bottom)
                        {
                            proposed = idx;
                            break; // first hit wins (stable)
                        }
                    }
                    bool pointerInsideOriginal = false;
                    if (!draggedOriginalRect.IsEmpty)
                    {
                        var m = RevertHoverHitMargin;
                        var expanded = new Rect(draggedOriginalRect.X - m, draggedOriginalRect.Y - m, draggedOriginalRect.Width + (2 * m), draggedOriginalRect.Height + (2 * m));
                        pointerInsideOriginal = _dragPointer.X >= expanded.X && _dragPointer.X <= expanded.Right && _dragPointer.Y >= expanded.Y && _dragPointer.Y <= expanded.Bottom;
                    }
                    if (proposed == DraggedIndex || pointerInsideOriginal)
                    {
                        // Only schedule revert if we have an active different target and pointer returned within original rect (with margin)
                        if (!_reorderSuppressed && DragTargetIndex != DraggedIndex && pointerInsideOriginal)
                        {
                            if (state.HoverCandidateIndex != DraggedIndex)
                            {
                                state.HoverCandidateIndex = DraggedIndex;
                                RestartHoverTimer(state);
                                if (EnableDiagnostics)
                                {
                                    Debug.WriteLine($"[WidgetLayout] Schedule revert hover (strict) Drag={DraggedIndex} CurrentTarget={DragTargetIndex} Pointer={_dragPointer}");
                                }
                            }
                        }
                        else
                        {
                            if (state.HoverCandidateIndex != -1)
                            {
                                StopHoverTimer(state);
                                state.HoverCandidateIndex = -1;
                            }
                        }
                    }
                    else if (proposed != DragTargetIndex)
                    {
                        if (state.HoverCandidateIndex != proposed)
                        {
                            state.HoverCandidateIndex = proposed;
                            RestartHoverTimer(state);
                            if (EnableDiagnostics)
                            {
                                Debug.WriteLine($"[WidgetLayout] Schedule commit hover (strict) Drag={DraggedIndex} Proposed={proposed} Pointer={_dragPointer}");
                            }
                        }
                    }
                }
                else if (!_isDragging)
                {
                    StopHoverTimer(state);
                    state.HoverCandidateIndex = -1;
                }

                int count = context.ItemCount;
                state.ArrangePassesSinceLastMeasure++;
                if (state.ArrangePassesSinceLastMeasure > ArrangePassGuardThreshold && state.ImplicitAnimations != null)
                {
                    if (EnableDiagnostics)
                    {
                        Debug.WriteLine("[WidgetLayout] Arrange guard triggered");
                    }

                    state.ImplicitAnimations = null;
                    state.AnimationAssigned.Clear();
                }

                // Reorder logic: if dragging and target valid -> simulate layout to determine new positions
                bool reorderActive = !_reorderSuppressed && _isDragging && _dragMovementActivated && DraggedIndex >= 0 && DragTargetIndex >= 0 && DragTargetIndex != DraggedIndex && DragTargetIndex < count;
                Dictionary<int, Rect> newRects = null; // maps original item index -> new rect when reordering
                Rect placeholderRect = new Rect();
                bool havePlaceholder = false;
                if (reorderActive)
                {
                    try
                    {
                        // Preserve previous dragged rect so we can keep updating its visual during drag
                        Rect previousDraggedRect = default(Rect);
                        bool hadDraggedRect = state.IndexToRect.TryGetValue(DraggedIndex, out previousDraggedRect);
                        newRects = new Dictionary<int, Rect>(count - 1);
                        int columns = state.EffectiveColumns;
                        double colW = ColumnWidth;
                        double rowH = RowHeight;
                        double spacing = Spacing;
                        double horizontalOffset = state.HorizontalOffset;
                        int dragColSpan = 1, dragRowSpan = 1;
                        FrameworkElement draggedEl = null;
                        try
                        {
                            draggedEl = context.GetOrCreateElementAt(DraggedIndex) as FrameworkElement;
                        }
                        catch { }
                        if (draggedEl != null)
                        {
                            dragColSpan = Math.Min(Math.Max(1, GetColumnSpan(draggedEl)), columns);
                            dragRowSpan = Math.Max(1, GetRowSpan(draggedEl));
                        }
                        int insertionIndex = DragTargetIndex;
                        if (insertionIndex < 0)
                        {
                            insertionIndex = 0;
                        }

                        int remainingCount = count - 1;
                        if (insertionIndex > remainingCount)
                        {
                            insertionIndex = remainingCount;
                        }

                        const int PLACEHOLDER = -1;
                        var order = new List<int>(count);
                        for (int i = 0; i < count; i++)
                        {
                            if (i == DraggedIndex)
                            {
                                continue;
                            }

                            order.Add(i);
                        }
                        order.Insert(insertionIndex, PLACEHOLDER);
                        var occupied = new HashSet<(int c, int r)>();
                        int maxRow = -1;
                        foreach (var idx in order)
                        {
                            int colSpan = 1, rowSpan = 1;
                            if (idx == PLACEHOLDER)
                            {
                                colSpan = dragColSpan;
                                rowSpan = dragRowSpan;
                            }
                            else
                            {
                                FrameworkElement el = null;
                                try
                                {
                                    if (idx < MaxMeasureRealizationCount)
                                    {
                                        el = context.GetOrCreateElementAt(idx) as FrameworkElement;
                                    }
                                }
                                catch { }
                                if (el != null)
                                {
                                    colSpan = Math.Min(Math.Max(1, GetColumnSpan(el)), columns);
                                    rowSpan = Math.Max(1, GetRowSpan(el));
                                }
                            }
                            int placedCol = 0, placedRow = 0;
                            bool found = false;
                            for (int r = 0; !found; r++)
                            {
                                for (int c = 0; c < columns; c++)
                                {
                                    if (CanPlace(c, r, colSpan, rowSpan, occupied, columns))
                                    {
                                        placedCol = c;
                                        placedRow = r;
                                        found = true;
                                        break;
                                    }
                                }
                            }
                            for (int r = placedRow; r < placedRow + rowSpan; r++)
                            {
                                for (int c = placedCol; c < placedCol + colSpan; c++)
                                {
                                    occupied.Add((c, r));
                                }
                            }

                            maxRow = Math.Max(maxRow, placedRow + rowSpan - 1);
                            double width = (colSpan * colW) + ((colSpan - 1) * spacing);
                            double height = (rowSpan * rowH) + ((rowSpan - 1) * spacing);
                            var rect = new Rect(horizontalOffset + (placedCol * (colW + spacing)), placedRow * (rowH + spacing), width, height);
                            if (idx == PLACEHOLDER)
                            {
                                placeholderRect = rect;
                                havePlaceholder = true;
                            }
                            else
                            {
                                newRects[idx] = rect;
                            }
                        }
                        // Update live hit-test map while dragging so internal target inference matches simulated positions
                        state.IndexToRect.Clear();
                        for (int i = 0; i < order.Count; i++)
                        {
                            var oi = order[i];
                            if (oi == PLACEHOLDER)
                            {
                                continue;
                            }
                            // Only add non-dragged items here; dragged item added after to ensure it remains present
                            if (oi != DraggedIndex)
                            {
                                state.IndexToRect[oi] = newRects[oi];
                            }
                        }
                        // Re-add dragged index so Arrange loop still updates its visual continuously
                        if (hadDraggedRect)
                        {
                            state.IndexToRect[DraggedIndex] = previousDraggedRect;
                        }
                        else if (draggedEl != null)
                        {
                            // Fallback size using measured/arranged size
                            var size = draggedEl.RenderSize;
                            state.IndexToRect[DraggedIndex] = new Rect(previousDraggedRect.X, previousDraggedRect.Y, size.Width > 0 ? size.Width : (dragColSpan * colW), size.Height > 0 ? size.Height : (dragRowSpan * rowH));
                        }
                        if (EnableDiagnostics)
                        {
                            Debug.WriteLine($"[WidgetLayout] Reflow Simulation Drag={DraggedIndex} -> {DragTargetIndex} InsertIndex={insertionIndex} Placeholder={(havePlaceholder ? placeholderRect.ToString() : "none")} Order=[{string.Join(",", order)}] (DraggedRectRetained={hadDraggedRect})");
                        }
                    }
                    catch (Exception simEx)
                    {
                        if (EnableDiagnostics)
                        {
                            Debug.WriteLine("[WidgetLayout] Reflow simulation error: " + simEx.Message);
                        }
                        newRects = null;
                        reorderActive = false;
                    }
                }

                // If after simulation some indices are missing (should not) patch them using previous layout snapshot
                if (reorderActive && EnableDiagnostics)
                {
                    int missingPostSim = 0;
                    for (int iCheck = 0; iCheck < count; iCheck++)
                    {
                        if (!state.IndexToRect.ContainsKey(iCheck))
                        {
                            missingPostSim++;
                        }
                    }
                    if (missingPostSim > 0)
                    {
                        Debug.WriteLine($"[WidgetLayout] PostSimMissing count={missingPostSim}; attempting patch from preDrag snapshot");
                        if (_preDragRects != null)
                        {
                            foreach (var kv in _preDragRects)
                            {
                                if (!state.IndexToRect.ContainsKey(kv.Key))
                                {
                                    state.IndexToRect[kv.Key] = kv.Value; // fallback
                                    if (EnableDiagnostics)
                                    {
                                        Debug.WriteLine($"[WidgetLayout] Patched rect for index {kv.Key} from pre-drag snapshot");
                                    }
                                }
                            }
                        }
                    }
                }

                // (Legacy) mapping removed; we now rely on spatial recomputation newRects when reorderActive

                foreach (var kvp in state.IndexToRect)
                {
                    int logicalIndex = kvp.Key;
                    FrameworkElement element = null;
                    try
                    {
                        element = context.GetOrCreateElementAt(logicalIndex) as FrameworkElement;
                    }
                    catch (Exception ex)
                    {
                        if (EnableDiagnostics)
                        {
                            Debug.WriteLine("[WidgetLayout] Arrange element creation failed: " + ex.Message);
                        }
                    }
                    if (element == null)
                    {
                        continue;
                    }

                    Rect baseRect = kvp.Value;
                    if (reorderActive && logicalIndex != DraggedIndex && newRects != null && newRects.TryGetValue(logicalIndex, out var nr))
                    {
                        baseRect = nr;
                    }
                    element.Arrange(new Rect(0, 0, baseRect.Width, baseRect.Height));
                    var visual = ElementCompositionPreview.GetElementVisual(element);
                    if (logicalIndex == DraggedIndex && _isDragging)
                    {
                        if (_dragPointer.X >= 0 && _dragPointer.Y >= 0)
                        {
                            // If we have a simulated placeholder, clamp within its bounds plane for nicer feel (optional)
                            double desiredX = _dragPointer.X - (baseRect.Width / 2.0);
                            double desiredY = _dragPointer.Y - (baseRect.Height / 2.0);
                            double maxX = Math.Max(0, state.TotalWidth - baseRect.Width) + (state.HorizontalOffset * 2);
                            double maxY = Math.Max(0, state.TotalHeight - baseRect.Height);
                            double clampedX = Math.Min(Math.Max(0, desiredX), maxX);
                            double clampedY = Math.Min(Math.Max(0, desiredY), maxY);
                            visual.Offset = new System.Numerics.Vector3((float)clampedX, (float)clampedY, 0);
                        }
                        else
                        {
                            visual.Offset = new System.Numerics.Vector3((float)baseRect.X, (float)baseRect.Y, 0);
                        }
                        visual.ImplicitAnimations = null;
                    }
                    else
                    {
                        var target = new System.Numerics.Vector3((float)baseRect.X, (float)baseRect.Y, 0);
                        if (visual.Offset.X != target.X || visual.Offset.Y != target.Y)
                        {
                            visual.Offset = target;
                        }

                        if (state.ImplicitAnimations != null && !state.AnimationAssigned.Contains(element))
                        {
                            visual.ImplicitAnimations = state.ImplicitAnimations;
                            state.AnimationAssigned.Add(element);
                        }
                    }
                }
                // Optionally draw placeholder diagnostics
                if (EnableDiagnostics && reorderActive && havePlaceholder)
                {
                    Debug.WriteLine($"[WidgetLayout] PlaceholderRect={placeholderRect}");
                }
                if (EnableDiagnostics && _isDragging)
                {
                    if (_lastLoggedDragIndex != DraggedIndex || _lastLoggedTargetIndex != DragTargetIndex || _lastLoggedHoverCandidate != state.HoverCandidateIndex)
                    {
                        Debug.WriteLine($"[WidgetLayout] DragState Dragged={DraggedIndex} Target={DragTargetIndex} HoverCand={state.HoverCandidateIndex} Suppressed={_reorderSuppressed} Activated={_dragMovementActivated}");
                        _lastLoggedDragIndex = DraggedIndex;
                        _lastLoggedTargetIndex = DragTargetIndex;
                        _lastLoggedHoverCandidate = state.HoverCandidateIndex;
                    }
                }

                // If a previous drag committed a reorder, force a clean remap by invalidating measure once.
                if (_forceRemapAfterDrag)
                {
                    _forceRemapAfterDrag = false;
                    // Clear any stale rects and re-measure immediately to repopulate real layout order.
                    state.IndexToRect.Clear();
                    state.Arranging = false; // release flag so InvalidateMeasure can proceed
                    InvalidateMeasure();
                    return finalSize; // skip this arrange; new measure+arrange cycle will follow
                }

                // Post-commit position fix: after a committed move and fresh measure, validate target rect occupancy
                if (_pendingPostCommitFix && !_isDragging)
                {
                    _pendingPostCommitFix = false; // run once
                    if (_lastCommitFrom >= 0 && _lastCommitTo >= 0 && state.IndexToRect.Count > 0)
                    {
                        if (EnableDiagnostics)
                        {
                            Debug.WriteLine($"[WidgetLayout] PostCommitFix From={_lastCommitFrom} To={_lastCommitTo}");
                        }
                        // Validate that every logical index 0..ItemCount-1 has a rect (otherwise we may have lost one)
                        int missing = 0;
                        for (int i = 0; i < context.ItemCount; i++)
                        {
                            if (!state.IndexToRect.ContainsKey(i))
                            {
                                missing++;
                            }
                        }

                        if (missing > 0 && EnableDiagnostics)
                        {
                            Debug.WriteLine($"[WidgetLayout] PostCommitFix MissingRects={missing}; forcing full remeasure");
                            state.IndexToRect.Clear();
                            state.Arranging = false;
                            InvalidateMeasure();
                            return finalSize;
                        }
                        // Extra overlap detection (simple O(n^2) for diagnostics only)
                        if (EnableDiagnostics)
                        {
                            int overlaps = 0;
                            var keys = new List<int>(state.IndexToRect.Keys);
                            for (int a = 0; a < keys.Count; a++)
                            {
                                for (int b = a + 1; b < keys.Count; b++)
                                {
                                    var ra = state.IndexToRect[keys[a]];
                                    var rb = state.IndexToRect[keys[b]];
                                    bool inter = ra.X < rb.X + rb.Width && ra.X + ra.Width > rb.X && ra.Y < rb.Y + rb.Height && ra.Y + ra.Height > rb.Y;
                                    if (inter && !(ra.Width == 0 || ra.Height == 0 || rb.Width == 0 || rb.Height == 0))
                                    {
                                        overlaps++;
                                    }
                                }
                            }
                            if (overlaps > 0)
                            {
                                Debug.WriteLine($"[WidgetLayout] PostCommitFix OverlapPairs={overlaps}");
                            }
                        }
                    }
                }
                if (_pendingCommitFinalize && !_isDragging)
                {
                    // After measure with new order, we want affected item to animate from old (cached) offset to new rect.
                    _pendingCommitFinalize = false;
                    if (EnableDiagnostics)
                    {
                        Debug.WriteLine($"[WidgetLayout] CommitFinalize From={_pendingCommitFrom} To={_pendingCommitTo}");
                    }
                    // Ensure implicit animations exist
                    EnsureImplicitAnimations(state, context);
                    // Mark both indices to reassign animation if still realized
                    if (_pendingCommitFrom >= 0)
                    {
                        state.AnimationAssigned.Remove(context.GetOrCreateElementAt(_pendingCommitFrom));
                    }

                    if (_pendingCommitTo >= 0)
                    {
                        state.AnimationAssigned.Remove(context.GetOrCreateElementAt(_pendingCommitTo));
                    }

                    _lastHoverCommittedTarget = -1; // clear after finalize
                }

                return finalSize;
            }
            finally { state.Arranging = false; }
        }

        private void RestartHoverTimer(LayoutState state)
        {
            StopHoverTimer(state);
            if (state.HoverCandidateIndex < 0)
            {
                return;
            }

            if (state.HoverTimer == null)
            {
                state.HoverTimer = new DispatcherTimer();
                state.HoverTimer.Tick += (s, e) =>
                {
                    state.HoverTimer.Stop();
                    if (_isDragging && _dragMovementActivated && state.HoverCandidateIndex >= 0 && state.HoverCandidateIndex != DragTargetIndex)
                    {
                        var candidate = state.HoverCandidateIndex;
                        bool wasForwardBefore = DragTargetIndex != DraggedIndex && DragTargetIndex >= 0;
                        bool isRevert = candidate == DraggedIndex; // returning to original slot
                        int previousTarget = DragTargetIndex;
                        DragTargetIndex = candidate;
                        _reorderSuppressed = false; // allow mapping now
                        _hoverCommitted = true;
                        if (!isRevert && DragTargetIndex != DraggedIndex)
                        {
                            _lastHoverCommittedTarget = DragTargetIndex; // remember last forward commit target
                        }
                        if (_preDragRects == null)
                        {
                            _preDragRects = new Dictionary<int, Rect>(state.IndexToRect);
                        }
                        // If this is a revert (back to original index) restore the original snapshot so subsequent forward hovers work again
                        if (isRevert && _preDragRects != null)
                        {
                            state.IndexToRect.Clear();
                            foreach (var kv in _preDragRects)
                            {
                                state.IndexToRect[kv.Key] = kv.Value;
                            }
                            // Clear last forward commit so we can commit again later
                            _lastHoverCommittedTarget = -1;
                            if (EnableDiagnostics)
                            {
                                Debug.WriteLine($"[WidgetLayout] HoverRevert Drag={DraggedIndex} PrevTarget={previousTarget} -> RestoredSnapshot({state.IndexToRect.Count})");
                            }
                        }
                        else if (EnableDiagnostics)
                        {
                            Debug.WriteLine($"[WidgetLayout] HoverCommit Drag={DraggedIndex} -> Target={DragTargetIndex} (PrevTarget={previousTarget} ForwardBefore={wasForwardBefore} IsRevert={isRevert})");
                        }
                        InvalidateArrange();
                    }
                    else if (_isDragging && _dragMovementActivated && state.HoverCandidateIndex >= 0)
                    {
                        // Timer fired but no change; log why
                        if (EnableDiagnostics)
                        {
                            Debug.WriteLine($"[WidgetLayout] HoverTimerNoOp Drag={DraggedIndex} Cand={state.HoverCandidateIndex} CurrentTarget={DragTargetIndex} Suppressed={_reorderSuppressed} Activated={_dragMovementActivated}");
                        }
                    }
                };
            }
            state.HoverTimer.Interval = TimeSpan.FromMilliseconds(DragHoverCommitDelay);
            state.HoverTimer.Start();
        }

        private void StopHoverTimer(LayoutState state)
        {
            state.HoverTimer?.Stop();
        }

        private void EnsureImplicitAnimations(LayoutState state, VirtualizingLayoutContext context)
        {
            if (state.ImplicitAnimations != null)
            {
                return;
            }

            RecreateImplicitAnimations(state, context, false);
        }

        private void RecreateImplicitAnimations(LayoutState state, VirtualizingLayoutContext context, bool isReflow)
        {
            if (context.ItemCount == 0)
            {
                return;
            }

            var element = context.GetOrCreateElementAt(0);
            var compositor = ElementCompositionPreview.GetElementVisual(element).Compositor;
            var easing = compositor.CreateCubicBezierEasingFunction(new System.Numerics.Vector2(0.4f, 0.0f), new System.Numerics.Vector2(0.2f, 1.0f));
            var offsetAnimation = compositor.CreateVector3KeyFrameAnimation();
            offsetAnimation.Target = "Offset";
            offsetAnimation.InsertExpressionKeyFrame(1f, "this.FinalValue", easing);
            offsetAnimation.Duration = TimeSpan.FromMilliseconds(isReflow ? ReflowAnimationDuration : StandardAnimationDuration);
            var collection = compositor.CreateImplicitAnimationCollection();
            collection["Offset"] = offsetAnimation;
            state.ImplicitAnimations = collection;
            state.AnimationAssigned.Clear();
        }

        /// <summary>
        /// Gets or sets a value indicating whether external UpdateDragTarget calls are honored during drag
        /// </summary>
        public bool UseExternalTargetUpdatesDuringDrag
        {
            get
            {
                return (bool)GetValue(UseExternalTargetUpdatesDuringDragProperty);
            }
            set
            {
                SetValue(UseExternalTargetUpdatesDuringDragProperty, value);
            }
        }
        public static readonly DependencyProperty UseExternalTargetUpdatesDuringDragProperty = DependencyProperty.Register(
            nameof(UseExternalTargetUpdatesDuringDrag), typeof(bool), typeof(WidgetLayout), new PropertyMetadata(false));

        /// <summary>
        /// Gets or sets the RevertHoverHitMargin
        /// </summary>
        public double RevertHoverHitMargin
        {
            get
            {
                return (double)GetValue(RevertHoverHitMarginProperty);
            }
            set
            {
                SetValue(RevertHoverHitMarginProperty, value);
            }
        }

        /// <summary>
        /// Defines the RevertHoverHitMarginProperty
        /// </summary>
        public static readonly DependencyProperty RevertHoverHitMarginProperty = DependencyProperty.Register(
            nameof(RevertHoverHitMargin), typeof(double), typeof(WidgetLayout), new PropertyMetadata(4d));
    }

    /// <summary>
    /// Defines the HorizontalLayoutMode
    /// </summary>
    public enum HorizontalLayoutMode
    {
        Left,
        Center,
        Right
    }
}
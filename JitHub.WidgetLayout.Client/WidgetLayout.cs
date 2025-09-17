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

    /// <summary>
    /// Defines the <see cref="WidgetLayout" />
    /// </summary>
    public class WidgetLayout : VirtualizingLayout
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
            DragTargetIndex = index;
            _dragPointer = new Point(-1, -1);
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
            if (_isDragging)
            {
                _dragPointer = p;
                InvalidateArrange();
            }
        }

        /// <summary>
        /// The CompleteDrag
        /// </summary>
        /// <param name="commitMove">The commitMove<see cref="Action{int,int}"/></param>
        public void CompleteDrag(Action<int, int> commitMove)
        {
            if (DraggedIndex >= 0 && DragTargetIndex >= 0 && DraggedIndex != DragTargetIndex)
            {
                commitMove?.Invoke(DraggedIndex, DragTargetIndex);
            }

            CancelDrag();
        }

        /// <summary>
        /// The CancelDrag
        /// </summary>
        public void CancelDrag()
        {
            _isDragging = false;
            DraggedIndex = -1;
            DragTargetIndex = -1;
            _dragPointer = new Point(-1, -1);
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
        /// Defines the _lastHorizontalOffset
        /// </summary>
        private double _lastHorizontalOffset;

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
                if (EnableDiagnostics)
                {
                    Debug.WriteLine($"[WidgetLayout] Measure Items={count} AvailW={availableSize.Width} UsedW={usedWidth} Offset={horizontalOffset} Cols={columns} Changed={state.ColumnsChanged}");
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
            if (state.Arranging)
            {
                return finalSize;
            }

            state.Arranging = true;
            try
            {
                EnsureImplicitAnimations(state, context);
                if (state.ColumnsChanged)
                {
                    RecreateImplicitAnimations(state, context, true);
                    state.ColumnsChanged = false;
                }
                // NEW: determine target index based on pointer vs centers of other widgets
                if (_isDragging && DraggedIndex >= 0 && state.IndexToRect.Count > 0 && _dragPointer.X >= 0 && _dragPointer.Y >= 0)
                {
                    int proposed = DraggedIndex;
                    foreach (var kv in state.IndexToRect)
                    {
                        int idx = kv.Key;
                        if (idx == DraggedIndex)
                        {
                            continue;
                        }

                        var rect = kv.Value;
                        if (_dragPointer.X >= rect.X && _dragPointer.X <= rect.X + rect.Width &&
                           _dragPointer.Y >= rect.Y && _dragPointer.Y <= rect.Y + rect.Height)
                        {
                            double centerX = rect.X + (rect.Width / 2.0);
                            double centerY = rect.Y + (rect.Height / 2.0);
                            if (DraggedIndex < idx)
                            {
                                // moving forward; cross center to adopt this position
                                if (_dragPointer.X >= centerX || _dragPointer.Y >= centerY)
                                {
                                    proposed = idx;
                                }
                            }
                            else if (DraggedIndex > idx)
                            {
                                // moving backward; cross center going upward/left
                                if (_dragPointer.X <= centerX || _dragPointer.Y <= centerY)
                                {
                                    proposed = idx;
                                }
                            }
                        }
                    }
                    if (proposed != DragTargetIndex)
                    {
                        DragTargetIndex = proposed;
                        // no need to InvalidateArrange again inside arrange pass
                    }
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
                bool hasMapping = false;
                Func<int, int> map = i => i;
                if (DraggedIndex >= 0 && DragTargetIndex >= 0 && DraggedIndex != DragTargetIndex && DragTargetIndex < count)
                {
                    hasMapping = true;
                    map = i =>
                    {
                        if (i == DraggedIndex)
                        {
                            return DragTargetIndex;
                        }
                        if (DraggedIndex < DragTargetIndex)
                        {
                            if (i > DraggedIndex && i <= DragTargetIndex)
                            {
                                return i - 1;
                            }
                        }
                        else
                        {
                            if (i >= DragTargetIndex && i < DraggedIndex)
                            {
                                return i + 1;
                            }
                        }
                        return i;
                    };
                }
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
                    if (hasMapping && logicalIndex != DraggedIndex)
                    {
                        int mapped = map(logicalIndex);
                        if (state.IndexToRect.ContainsKey(mapped))
                        {
                            baseRect = state.IndexToRect[mapped];
                        }
                    }
                    element.Arrange(new Rect(0, 0, baseRect.Width, baseRect.Height));
                    var visual = ElementCompositionPreview.GetElementVisual(element);
                    if (logicalIndex == DraggedIndex && _isDragging)
                    {
                        if (_dragPointer.X >= 0 && _dragPointer.Y >= 0)
                        {
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
                if (EnableDiagnostics)
                {
                    Debug.WriteLine($"[WidgetLayout] Arrange Realized={state.IndexToRect.Count} Drag=({DraggedIndex}->{DragTargetIndex}) Offset={state.HorizontalOffset} Cols={state.EffectiveColumns}");
                }

                return finalSize;
            }
            finally { state.Arranging = false; }
        }

        /// <summary>
        /// The EnsureImplicitAnimations
        /// </summary>
        /// <param name="state">The state<see cref="LayoutState"/></param>
        /// <param name="context">The context<see cref="VirtualizingLayoutContext"/></param>
        private void EnsureImplicitAnimations(LayoutState state, VirtualizingLayoutContext context)
        {
            if (state.ImplicitAnimations != null)
            {
                return;
            }

            RecreateImplicitAnimations(state, context, false);
        }

        /// <summary>
        /// The RecreateImplicitAnimations
        /// </summary>
        /// <param name="state">The state<see cref="LayoutState"/></param>
        /// <param name="context">The context<see cref="VirtualizingLayoutContext"/></param>
        /// <param name="isReflow">The isReflow<see cref="bool"/></param>
        private void RecreateImplicitAnimations(LayoutState state, VirtualizingLayoutContext context, bool isReflow)
        {
            if (context.ItemCount == 0)
            {
                return;
            }

            var element = context.GetOrCreateElementAt(0);
            var compositor = ElementCompositionPreview.GetElementVisual(element).Compositor;
            var offsetAnimation = compositor.CreateVector3KeyFrameAnimation();
            offsetAnimation.Target = "Offset";
            offsetAnimation.InsertExpressionKeyFrame(1f, "this.FinalValue");
            offsetAnimation.Duration = TimeSpan.FromMilliseconds(isReflow ? ReflowAnimationDuration : StandardAnimationDuration);
            var collection = compositor.CreateImplicitAnimationCollection();
            collection["Offset"] = offsetAnimation;
            state.ImplicitAnimations = collection;
            state.AnimationAssigned.Clear();
        }
    }

    /// <summary>
    /// Defines the HorizontalLayoutMode
    /// </summary>
    public enum HorizontalLayoutMode
    {
        ///<summary>
        /// Defines the Left
        /// </summary>

        /// <summary>
        /// Defines the Left
        /// </summary>
        Left,

        /// <summary>
        /// Defines the Center
        /// </summary>
        Center,

        /// <summary>
        /// Defines the Right
        /// </summary>
        Right
    }
}
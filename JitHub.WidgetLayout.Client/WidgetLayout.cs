using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq;
using Windows.Foundation;
using Windows.UI.Composition;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls; // for UIElement
using Windows.UI.Xaml.Hosting; // ElementCompositionPreview
using Microsoft.UI.Xaml.Controls; // VirtualizingLayout lives here in WinUI 2

namespace JitHub.WidgetLayout.Client
{
    public class WidgetLayout : VirtualizingLayout
    {
        #region Dependency Properties
        public int Columns
        {
            get => (int)GetValue(ColumnsProperty);
            set => SetValue(ColumnsProperty, value);
        }
        public static readonly DependencyProperty ColumnsProperty =
            DependencyProperty.Register(nameof(Columns), typeof(int), typeof(WidgetLayout), new PropertyMetadata(4, OnLayoutPropertyChanged));

        public double ColumnWidth
        {
            get => (double)GetValue(ColumnWidthProperty);
            set => SetValue(ColumnWidthProperty, value);
        }
        public static readonly DependencyProperty ColumnWidthProperty =
            DependencyProperty.Register(nameof(ColumnWidth), typeof(double), typeof(WidgetLayout), new PropertyMetadata(140d, OnLayoutPropertyChanged));

        public double RowHeight
        {
            get => (double)GetValue(RowHeightProperty);
            set => SetValue(RowHeightProperty, value);
        }
        public static readonly DependencyProperty RowHeightProperty =
            DependencyProperty.Register(nameof(RowHeight), typeof(double), typeof(WidgetLayout), new PropertyMetadata(140d, OnLayoutPropertyChanged));

        public double Spacing
        {
            get => (double)GetValue(SpacingProperty);
            set => SetValue(SpacingProperty, value);
        }
        public static readonly DependencyProperty SpacingProperty =
            DependencyProperty.Register(nameof(Spacing), typeof(double), typeof(WidgetLayout), new PropertyMetadata(12d, OnLayoutPropertyChanged));

        public bool EnableDiagnostics
        {
            get => (bool)GetValue(EnableDiagnosticsProperty);
            set => SetValue(EnableDiagnosticsProperty, value);
        }
        public static readonly DependencyProperty EnableDiagnosticsProperty =
            DependencyProperty.Register(nameof(EnableDiagnostics), typeof(bool), typeof(WidgetLayout), new PropertyMetadata(false));

        public int MaxMeasureRealizationCount
        {
            get => (int)GetValue(MaxMeasureRealizationCountProperty);
            set => SetValue(MaxMeasureRealizationCountProperty, value);
        }
        public static readonly DependencyProperty MaxMeasureRealizationCountProperty =
            DependencyProperty.Register(nameof(MaxMeasureRealizationCount), typeof(int), typeof(WidgetLayout), new PropertyMetadata(200));
        #endregion

        #region Attached Properties (Spans)
        public static int GetColumnSpan(DependencyObject obj) => (int)obj.GetValue(ColumnSpanProperty);
        public static void SetColumnSpan(DependencyObject obj, int value) => obj.SetValue(ColumnSpanProperty, value);
        public static readonly DependencyProperty ColumnSpanProperty =
            DependencyProperty.RegisterAttached("ColumnSpan", typeof(int), typeof(WidgetLayout), new PropertyMetadata(1, OnLayoutPropertyChanged));

        public static int GetRowSpan(DependencyObject obj) => (int)obj.GetValue(RowSpanProperty);
        public static void SetRowSpan(DependencyObject obj, int value) => obj.SetValue(RowSpanProperty, value);
        public static readonly DependencyProperty RowSpanProperty =
            DependencyProperty.RegisterAttached("RowSpan", typeof(int), typeof(WidgetLayout), new PropertyMetadata(1, OnLayoutPropertyChanged));
        #endregion

        #region Drag / Reorder State API
        public int DraggedIndex { get; private set; } = -1;
        public int DragTargetIndex { get; private set; } = -1;

        public void BeginDrag(int index)
        {
            _isDragging = true;
            DraggedIndex = index;
            DragTargetIndex = index;
            _dragPointer = new Point(-1, -1);
            InvalidateArrange();
        }
        public void UpdateDragTarget(int targetIndex)
        {
            if (DraggedIndex >= 0)
            {
                targetIndex = Math.Max(0, targetIndex);
                if (targetIndex == DragTargetIndex) return; // avoid redundant passes
                DragTargetIndex = targetIndex;
                InvalidateArrange();
            }
        }
        public void UpdateDragPointer(Point p)
        {
            if (_isDragging)
            {
                _dragPointer = p;
                InvalidateArrange();
            }
        }
        public void CompleteDrag(Action<int, int> commitMove)
        {
            if (DraggedIndex >= 0 && DragTargetIndex >= 0 && DraggedIndex != DragTargetIndex)
            {
                commitMove?.Invoke(DraggedIndex, DragTargetIndex);
            }
            CancelDrag();
        }
        public void CancelDrag()
        {
            _isDragging = false;
            DraggedIndex = -1;
            DragTargetIndex = -1;
            _dragPointer = new Point(-1, -1);
            InvalidateArrange();
        }
        #endregion

        private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var layout = d as WidgetLayout;
            if (layout != null)
            {
                layout.InvalidateMeasure();
            }
        }

        #region Internal LayoutState
        private class LayoutState
        {
            public Dictionary<int, Rect> IndexToRect = new Dictionary<int, Rect>();
            public ImplicitAnimationCollection ImplicitAnimations;
            public bool Measuring;
            public bool Arranging;
            public HashSet<UIElement> AnimationAssigned = new HashSet<UIElement>();
            public int ArrangePassesSinceLastMeasure;
            public double TotalWidth;
            public double TotalHeight;
        }
        #endregion

        private bool _isDragging; // runtime flag
        private const int ArrangePassGuardThreshold = 300; // safety limit
        private Point _dragPointer = new Point(-1, -1);

        protected override void InitializeForContextCore(VirtualizingLayoutContext context)
        {
            if (!(context.LayoutState is LayoutState))
            {
                context.LayoutState = new LayoutState();
            }
        }

        protected override void UninitializeForContextCore(VirtualizingLayoutContext context)
        {
            var ls = context.LayoutState as LayoutState;
            if (ls != null)
            {
                ls.IndexToRect.Clear();
                ls.ImplicitAnimations = null;
            }
        }

        protected override void OnItemsChangedCore(VirtualizingLayoutContext context, object source, NotifyCollectionChangedEventArgs args)
        {
            var ls = context.LayoutState as LayoutState;
            if (ls != null)
            {
                ls.IndexToRect.Clear();
            }
            InvalidateMeasure();
        }

        protected override Size MeasureOverride(VirtualizingLayoutContext context, Size availableSize)
        {
            var state = (LayoutState)context.LayoutState;
            if (state.Measuring) return availableSize; // reentrancy guard
            state.Measuring = true;
            try
            {
                state.IndexToRect.Clear();
                state.ArrangePassesSinceLastMeasure = 0; // reset counter each measure

                int columns = Math.Max(1, Columns);
                double colW = ColumnWidth;
                double rowH = RowHeight;
                double spacing = Spacing;
                int count = context.ItemCount;

                var occupied = new HashSet<(int c, int r)>();
                int maxRow = -1;

                for (int i = 0; i < count; i++)
                {
                    FrameworkElement element = null;
                    bool realize = i < MaxMeasureRealizationCount; // limit full realization to avoid freezes
                    if (realize)
                    {
                        try
                        {
                            element = context.GetOrCreateElementAt(i) as FrameworkElement;
                        }
                        catch (Exception ex)
                        {
                            if (EnableDiagnostics)
                            {
                                Debug.WriteLine("[WidgetLayout] GetOrCreateElementAt exception: " + ex.Message);
                            }
                        }
                    }
                    int colSpan = 1;
                    int rowSpan = 1;
                    if (element != null)
                    {
                        colSpan = Math.Min(Math.Max(1, GetColumnSpan(element)), columns);
                        rowSpan = Math.Max(1, GetRowSpan(element));
                    }

                    int placedCol = 0; int placedRow = 0; bool found = false;
                    for (int r = 0; !found; r++)
                    {
                        for (int c = 0; c < columns; c++)
                        {
                            if (CanPlace(c, r, colSpan, rowSpan, occupied, columns))
                            {
                                placedCol = c; placedRow = r; found = true; break;
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

                    double width = colSpan * colW + (colSpan - 1) * spacing;
                    double height = rowSpan * rowH + (rowSpan - 1) * spacing;
                    if (element != null)
                    {
                        element.Measure(new Size(width, height));
                    }
                    var rect = new Rect(placedCol * (colW + spacing), placedRow * (rowH + spacing), width, height);
                    state.IndexToRect[i] = rect;
                }

                double totalWidth = columns * colW + (columns - 1) * spacing;
                double totalHeight = (maxRow + 1) * rowH + Math.Max(0, maxRow) * spacing;
                state.TotalWidth = totalWidth;
                state.TotalHeight = totalHeight;

                if (EnableDiagnostics)
                {
                    Debug.WriteLine($"[WidgetLayout] Measure: Items={count} Realized={context.RealizationRect} Total=({totalWidth}x{totalHeight}) Drag=({DraggedIndex}->{DragTargetIndex})");
                }

                return new Size(totalWidth, totalHeight);
            }
            finally
            {
                state.Measuring = false;
            }
        }

        private static bool CanPlace(int startCol, int startRow, int colSpan, int rowSpan, HashSet<(int c, int r)> occupied, int columns)
        {
            if (startCol + colSpan > columns) return false;
            for (int r = startRow; r < startRow + rowSpan; r++)
            {
                for (int c = startCol; c < startCol + colSpan; c++)
                {
                    if (occupied.Contains((c, r))) return false;
                }
            }
            return true;
        }

        protected override Size ArrangeOverride(VirtualizingLayoutContext context, Size finalSize)
        {
            var state = (LayoutState)context.LayoutState;
            if (state.Arranging) return finalSize; // reentrancy guard
            state.Arranging = true;
            try
            {
                EnsureImplicitAnimations(state, context);
                int count = context.ItemCount;

                // Guard: if too many arrange passes without a fresh measure, disable animations (possible feedback loop)
                state.ArrangePassesSinceLastMeasure++;
                if (state.ArrangePassesSinceLastMeasure > ArrangePassGuardThreshold && state.ImplicitAnimations != null)
                {
                    if (EnableDiagnostics)
                    {
                        Debug.WriteLine("[WidgetLayout] Arrange guard triggered. Disabling implicit animations to break loop.");
                    }
                    state.ImplicitAnimations = null; // remove animations to stop feedback loop
                    state.AnimationAssigned.Clear();
                }

                Func<int, int> map = i => i;
                bool hasMapping = false;
                if (DraggedIndex >= 0 && DragTargetIndex >= 0 && DraggedIndex != DragTargetIndex && DragTargetIndex < count)
                {
                    hasMapping = true;
                    map = i =>
                    {
                        if (i == DraggedIndex) return DragTargetIndex;
                        if (DraggedIndex < DragTargetIndex)
                        {
                            if (i > DraggedIndex && i <= DragTargetIndex) return i - 1;
                        }
                        else
                        {
                            if (i >= DragTargetIndex && i < DraggedIndex) return i + 1;
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
                    if (element == null) continue;

                    Rect baseRect = kvp.Value;

                    // For non-dragged elements, if a mapping is active, display at mapped rect.
                    if (hasMapping && logicalIndex != DraggedIndex)
                    {
                        int mapped = map(logicalIndex);
                        if (state.IndexToRect.ContainsKey(mapped))
                        {
                            baseRect = state.IndexToRect[mapped];
                        }
                    }

                    // Arrange child at (0,0) with its size; use composition Offset for position to avoid XAML resetting positions.
                    element.Arrange(new Rect(0, 0, baseRect.Width, baseRect.Height));
                    var visual = ElementCompositionPreview.GetElementVisual(element);

                    if (logicalIndex == DraggedIndex && _isDragging)
                    {
                        if (_dragPointer.X >= 0 && _dragPointer.Y >= 0)
                        {
                            double desiredX = _dragPointer.X - baseRect.Width / 2.0;
                            double desiredY = _dragPointer.Y - baseRect.Height / 2.0;
                            double maxX = Math.Max(0, state.TotalWidth - baseRect.Width);
                            double maxY = Math.Max(0, state.TotalHeight - baseRect.Height);
                            double clampedX = Math.Min(Math.Max(0, desiredX), maxX);
                            double clampedY = Math.Min(Math.Max(0, desiredY), maxY);
                            visual.Offset = new System.Numerics.Vector3((float)clampedX, (float)clampedY, 0);
                        }
                        else
                        {
                            visual.Offset = new System.Numerics.Vector3((float)baseRect.X, (float)baseRect.Y, 0);
                        }
                        visual.ImplicitAnimations = null; // no animations for dragged item
                    }
                    else
                    {
                        // Non-dragged element: set its position via Offset
                        var targetOffset = new System.Numerics.Vector3((float)baseRect.X, (float)baseRect.Y, 0);
                        if (visual.Offset.X != targetOffset.X || visual.Offset.Y != targetOffset.Y)
                        {
                            visual.Offset = targetOffset; // triggers implicit animation if assigned
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
                    Debug.WriteLine($"[WidgetLayout] Arrange: RealizedCount={state.IndexToRect.Count} Drag=({DraggedIndex}->{DragTargetIndex}) DragPtr={_dragPointer}");
                }
                return finalSize;
            }
            finally
            {
                state.Arranging = false;
            }
        }

        private void EnsureImplicitAnimations(LayoutState state, VirtualizingLayoutContext context)
        {
            if (state.ImplicitAnimations != null) return;
            if (context.ItemCount == 0) return;
            var element = context.GetOrCreateElementAt(0);
            var compositor = ElementCompositionPreview.GetElementVisual(element).Compositor;

            var offsetAnimation = compositor.CreateVector3KeyFrameAnimation();
            offsetAnimation.Target = "Offset"; // required
            offsetAnimation.InsertExpressionKeyFrame(1f, "this.FinalValue");
            offsetAnimation.Duration = TimeSpan.FromMilliseconds(220);

            var collection = compositor.CreateImplicitAnimationCollection();
            collection["Offset"] = offsetAnimation;
            state.ImplicitAnimations = collection;
        }
    }
}

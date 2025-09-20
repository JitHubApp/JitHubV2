using Microsoft.UI.Xaml.Controls; // ItemsRepeater
using System.Diagnostics;
using Windows.Foundation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media; // VisualTreeHelper

namespace JitHub.Widget
{
    public sealed class WidgetContainer : ContentControl
    {
        private Grid _header;
        private WidgetLayout _layoutRef;
        private bool _dragging;
        private Point _startPoint;
        private int _index = -1;
        private long _layoutIsEditingCallbackToken = -1; // callback registration token
        private bool _loadedAttached;

        public WidgetContainer()
        {
            DefaultStyleKey = typeof(WidgetContainer);
            Loaded += WidgetContainer_Loaded; // fallback hookup
        }

        private void WidgetContainer_Loaded(object sender, RoutedEventArgs e)
        {
            EnsureLayoutReference("Loaded");
        }

        public WidgetLayout Layout
        {
            get => (WidgetLayout)GetValue(LayoutProperty);
            set => SetValue(LayoutProperty, value);
        }

        public static readonly DependencyProperty LayoutProperty = DependencyProperty.Register(
            nameof(Layout), typeof(WidgetLayout), typeof(WidgetContainer), new PropertyMetadata(null, OnLayoutChanged));

        private static void OnLayoutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var c = (WidgetContainer)d;
            if (c._layoutRef != null && c._layoutIsEditingCallbackToken != -1)
            {
                c._layoutRef.UnregisterPropertyChangedCallback(WidgetLayout.IsEditingProperty, c._layoutIsEditingCallbackToken);
                c._layoutIsEditingCallbackToken = -1;
            }
            c._layoutRef = e.NewValue as WidgetLayout;
            if (c._layoutRef != null)
            {
                c._layoutIsEditingCallbackToken = c._layoutRef.RegisterPropertyChangedCallback(WidgetLayout.IsEditingProperty, (s, args) =>
                {
                    Debug.WriteLine($"[WidgetContainer] Layout IsEditing changed -> {c._layoutRef.IsEditing} (container idx={c.TryGetIndexForLog()})");
                    c.UpdateEditingState();
                });
            }
            Debug.WriteLine($"[WidgetContainer] OnLayoutChanged new={(c._layoutRef != null)} IsEditing={(c._layoutRef?.IsEditing.ToString() ?? "null")} idx={c.TryGetIndexForLog()}");
            c.UpdateEditingState();
        }

        // Keeping (optional) local IsEditing for future but log changes
        public bool IsEditing
        {
            get => (bool)GetValue(IsEditingProperty);
            set => SetValue(IsEditingProperty, value);
        }

        public static readonly DependencyProperty IsEditingProperty = DependencyProperty.Register(
            nameof(IsEditing), typeof(bool), typeof(WidgetContainer), new PropertyMetadata(false, OnIsEditingChanged));

        private static void OnIsEditingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var c = (WidgetContainer)d;
            Debug.WriteLine($"[WidgetContainer] Local IsEditing changed {e.OldValue} -> {e.NewValue} idx={c.TryGetIndexForLog()}");
            c.UpdateEditingState();
        }

        private void UpdateEditingState()
        {
            if (_header != null)
            {
                bool active = _layoutRef != null && _layoutRef.IsEditing; // rely on layout
                _header.IsHitTestVisible = active;
                _header.Opacity = active ? 1.0 : 0.0;
                Debug.WriteLine($"[WidgetContainer] UpdateEditingState active={active} layoutIsEditing={_layoutRef?.IsEditing} localIsEditing={IsEditing} idx={TryGetIndexForLog()} layoutRef={( _layoutRef==null ? "null" : "set")}");
            }
        }

        protected override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            if (_header != null)
            {
                _header.PointerPressed -= Header_PointerPressed;
                _header.PointerMoved -= Header_PointerMoved;
                _header.PointerReleased -= Header_PointerReleased;
                _header.PointerCanceled -= Header_PointerReleased;
            }
            _header = GetTemplateChild("Header") as Grid;
            if (_header != null)
            {
                _header.PointerPressed += Header_PointerPressed;
                _header.PointerMoved += Header_PointerMoved;
                _header.PointerReleased += Header_PointerReleased;
                _header.PointerCanceled += Header_PointerReleased;
                Debug.WriteLine($"[WidgetContainer] OnApplyTemplate header found idx={TryGetIndexForLog()} layoutIsEditing={_layoutRef?.IsEditing}");
            }
            else
            {
                Debug.WriteLine("[WidgetContainer] OnApplyTemplate header NOT found");
            }
            // Attempt to auto-resolve layout if binding (ElementName) did not populate Layout DP
            EnsureLayoutReference("OnApplyTemplate");
            UpdateEditingState();
        }

        private void EnsureLayoutReference(string caller)
        {
            if (_layoutRef == null)
            {
                var rep = FindParentRepeater();
                var wl = rep?.Layout as WidgetLayout;
                if (wl != null)
                {
                    // Assign via DP to trigger callbacks
                    Layout = wl;
                    Debug.WriteLine($"[WidgetContainer] Auto-linked Layout via parent repeater ({caller}) idx={TryGetIndexForLog()}");
                }
                else
                {
                    Debug.WriteLine($"[WidgetContainer] EnsureLayoutReference failed ({caller}) idx={TryGetIndexForLog()} repeater={(rep!=null)} repLayoutType={rep?.Layout?.GetType().Name}");
                }
            }
        }

        private ItemsRepeater FindParentRepeater()
        {
            DependencyObject cur = this;
            while (cur != null)
            {
                if (cur is ItemsRepeater rpt)
                {
                    return rpt;
                }
                cur = VisualTreeHelper.GetParent(cur);
            }
            return null;
        }

        private string TryGetIndexForLog()
        {
            try
            {
                var rep = FindParentRepeater();
                if (rep != null)
                {
                    int idx = rep.GetElementIndex(this);
                    return idx.ToString();
                }
            }
            catch { }
            return "?";
        }

        private void Header_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (_layoutRef == null || !_layoutRef.IsEditing)
            {
                Debug.WriteLine($"[WidgetContainer] Header_PointerPressed ignored editing={_layoutRef?.IsEditing} hasLayout={_layoutRef!=null} idx={TryGetIndexForLog()}");
                // Try late binding one more time in case layout not yet resolved
                EnsureLayoutReference("PointerPressed");
                return;
            }
            var repeater = FindParentRepeater();
            if (repeater == null)
            {
                Debug.WriteLine("[WidgetContainer] Header_PointerPressed no repeater");
                return;
            }
            _index = repeater.GetElementIndex(this);
            if (_index < 0)
            {
                Debug.WriteLine("[WidgetContainer] Header_PointerPressed invalid index");
                return;
            }
            _dragging = true;
            _startPoint = e.GetCurrentPoint(repeater).Position;
            Debug.WriteLine($"[WidgetContainer] BeginDrag idx={_index} start={_startPoint}");
            _layoutRef.BeginDrag(_index);
            _header.CapturePointer(e.Pointer);
            e.Handled = true;
        }

        private void Header_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_dragging || _layoutRef == null)
            {
                return;
            }
            var repeater = FindParentRepeater();
            if (repeater == null)
            {
                return;
            }
            var p = e.GetCurrentPoint(repeater).Position;
            _layoutRef.UpdateDragPointer(p);
            var colWidth = _layoutRef.ColumnWidth + _layoutRef.Spacing;
            var rowHeight = _layoutRef.RowHeight + _layoutRef.Spacing;
            int col = (int)System.Math.Max(0, System.Math.Floor(p.X / colWidth));
            int row = (int)System.Math.Max(0, System.Math.Floor(p.Y / rowHeight));
            int targetIndex = (row * _layoutRef.Columns) + col;
            if (repeater.ItemsSource is System.Collections.ICollection coll && targetIndex >= coll.Count)
            {
                targetIndex = coll.Count - 1;
            }
            Debug.WriteLine($"[WidgetContainer] DragMove idx={_index} pointer={p} targetIndex={targetIndex}");
            _layoutRef.UpdateDragTarget(targetIndex);
            e.Handled = true;
        }

        private void Header_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_dragging || _layoutRef == null)
            {
                return;
            }
            _header.ReleasePointerCapture(e.Pointer);
            Debug.WriteLine($"[WidgetContainer] CompleteDrag idx={_index} (delegating reorder to ReorderRequested event)");
            // Pass null so we don't directly mutate collection; host should handle ReorderRequested.
            _layoutRef.CompleteDrag(null);
            _dragging = false;
            _index = -1;
            e.Handled = true;
        }
    }
}

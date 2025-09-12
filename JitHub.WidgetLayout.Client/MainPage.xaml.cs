using System;
using System.Collections.ObjectModel;
using System.Linq;
using Windows.Foundation; // Point
using Windows.System;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives; // ToggleButton
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Microsoft.UI.Xaml.Controls; // ItemsRepeater

namespace JitHub.WidgetLayout.Client
{
    public sealed partial class MainPage : Page
    {
        private readonly ObservableCollection<WidgetItem> _items = new ObservableCollection<WidgetItem>();
        private Point _dragStartPoint;
        private bool _dragging;
        private int _dragIndex = -1;

        private ItemsRepeater _repeater; // resolved at runtime
        private ToggleButton _editToggle;
        private WidgetLayout _layout;

        public MainPage()
        {
            InitializeComponent();
            // Resolve named XAML elements (no reliance on generated fields)
            _repeater = FindName("Repeater") as ItemsRepeater;
            _editToggle = FindName("EditToggle") as ToggleButton;
            _layout = FindName("LayoutRef") as WidgetLayout;

            LoadSampleData();
            if (_repeater != null)
            {
                _repeater.ItemsSource = _items;
            }
        }

        private bool IsEditMode => _editToggle != null && (_editToggle.IsChecked ?? false);

        private void LoadSampleData()
        {
            var rnd = new Random();
            string[] titles = { "Mail", "Calendar", "Weather", "Stocks", "News", "Tasks", "Music", "Photos", "Videos", "Notes", "Maps", "Health" };
            foreach (var t in titles)
            {
                _items.Add(new WidgetItem
                {
                    Title = t,
                    Color = new SolidColorBrush(Color.FromArgb(255, (byte)rnd.Next(30, 200), (byte)rnd.Next(30, 200), (byte)rnd.Next(30, 200)))
                });
            }
        }

        private void BeginDrag(FrameworkElement fe, PointerRoutedEventArgs e)
        {
            if (!IsEditMode || _repeater == null || _layout == null) return;
            _dragIndex = _repeater.GetElementIndex(fe);
            if (_dragIndex < 0) return;
            _dragging = true;
            _dragStartPoint = e.GetCurrentPoint(_repeater).Position;
            _layout.BeginDrag(_dragIndex);
            fe.CapturePointer(e.Pointer);
            e.Handled = true;
        }

        private void UpdateDrag(FrameworkElement fe, PointerRoutedEventArgs e)
        {
            if (!_dragging || _repeater == null || _layout == null) return;
            var p = e.GetCurrentPoint(_repeater).Position;
            // Update pointer for smooth translation of dragged item
            _layout.UpdateDragPointer(p);

            var colWidth = _layout.ColumnWidth + _layout.Spacing;
            var rowHeight = _layout.RowHeight + _layout.Spacing;
            int col = (int)Math.Max(0, Math.Floor(p.X / colWidth));
            int row = (int)Math.Max(0, Math.Floor(p.Y / rowHeight));
            int targetIndex = row * _layout.Columns + col;
            if (targetIndex >= _items.Count) targetIndex = _items.Count - 1;
            _layout.UpdateDragTarget(targetIndex);
            e.Handled = true;
        }

        private void EndDrag(FrameworkElement fe, PointerRoutedEventArgs e)
        {
            if (!_dragging || _layout == null) return;
            fe.ReleasePointerCapture(e.Pointer);
            _layout.CompleteDrag((oldIdx, newIdx) =>
            {
                if (oldIdx == newIdx) return;
                var item = _items[oldIdx];
                _items.RemoveAt(oldIdx);
                _items.Insert(newIdx, item);
            });
            _dragging = false;
            _dragIndex = -1;
            e.Handled = true;
        }

        // Event handlers wired in XAML
        private void Widget_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var fe = sender as FrameworkElement; if (fe == null) return; BeginDrag(fe, e);
        }
        private void Widget_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            var fe = sender as FrameworkElement; if (fe == null) return; if (_dragging) UpdateDrag(fe, e);
        }
        private void Widget_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            var fe = sender as FrameworkElement; if (fe == null) return; EndDrag(fe, e);
        }

        private void Widget_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (!IsEditMode || _repeater == null || _layout == null) return;
            var fe = sender as FrameworkElement; if (fe == null) return;
            int idx = _repeater.GetElementIndex(fe); if (idx < 0) return;
            int target = -1;
            switch (e.Key)
            {
                case VirtualKey.Left: target = Math.Max(0, idx - 1); break;
                case VirtualKey.Right: target = Math.Min(_items.Count - 1, idx + 1); break;
                case VirtualKey.Up: target = Math.Max(0, idx - _layout.Columns); break;
                case VirtualKey.Down: target = Math.Min(_items.Count - 1, idx + _layout.Columns); break;
            }
            if (target >= 0 && target != idx)
            {
                var item = _items[idx];
                _items.RemoveAt(idx);
                _items.Insert(target, item);
                e.Handled = true;
            }
        }

        private void Shuffle_Click(object sender, RoutedEventArgs e)
        {
            var rnd = new Random();
            var shuffled = _items.OrderBy(_ => rnd.Next()).ToList();
            _items.Clear();
            foreach (var it in shuffled) _items.Add(it);
        }

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            var rnd = new Random();
            _items.Add(new WidgetItem
            {
                Title = "New " + _items.Count,
                Color = new SolidColorBrush(Color.FromArgb(255, (byte)rnd.Next(30, 200), (byte)rnd.Next(30, 200), (byte)rnd.Next(30, 200)))
            });
        }

        private void Remove_Click(object sender, RoutedEventArgs e)
        {
            if (_items.Count > 0)
            {
                _items.RemoveAt(_items.Count - 1);
            }
        }
    }

    public class WidgetItem
    {
        public string Title { get; set; }
        public Brush Color { get; set; }
    }
}

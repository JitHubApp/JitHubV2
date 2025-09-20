using Microsoft.UI.Xaml.Controls; // ItemsRepeater
using System;
using System.Collections.ObjectModel;
using System.Linq;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives; // ToggleButton
using Windows.UI.Xaml.Media;
using JitHub.Widget; // library
using WidgetLayoutControl = JitHub.Widget.WidgetLayout; // alias to avoid namespace ambiguity

namespace JitHub.WidgetLayout.Client
{
    public sealed partial class MainPage : Page
    {
        private readonly ObservableCollection<WidgetItem> _items = new ObservableCollection<WidgetItem>();
        private ItemsRepeater _repeater; // resolved at runtime
        private ToggleButton _editToggle;
        private WidgetLayoutControl _layout;

        public MainPage()
        {
            InitializeComponent();
            _repeater = Repeater; // named element
            _layout = LayoutRef; // from XAML
            _editToggle = EditToggle;
            LoadSampleData();
            if (_repeater != null)
            {
                _repeater.ItemsSource = _items;
            }
            if (_layout != null)
            {
                _layout.ReorderRequested += Layout_ReorderRequested;
            }
        }

        private void Layout_ReorderRequested(object sender, WidgetLayoutControl.WidgetReorderEventArgs e)
        {
            if (e.OldIndex == e.NewIndex || e.OldIndex < 0 || e.NewIndex < 0 || e.OldIndex >= _items.Count || e.NewIndex >= _items.Count)
            {
                return;
            }
            var item = _items[e.OldIndex];
            _items.RemoveAt(e.OldIndex);
            _items.Insert(e.NewIndex, item);
        }

        private bool IsEditMode => _layout != null && _layout.IsEditing;

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

        private void Shuffle_Click(object sender, RoutedEventArgs e)
        {
            var rnd = new Random();
            var shuffled = _items.OrderBy(_ => rnd.Next()).ToList();
            _items.Clear();
            foreach (var it in shuffled)
            {
                _items.Add(it);
            }
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

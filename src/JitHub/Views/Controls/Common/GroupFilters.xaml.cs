using JitHub.Models;
using JitHub.Models.Filter;
using System.Collections.Generic;
using System.Windows.Input;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.Views.Controls.Common
{
    public sealed partial class GroupFilters : UserControl
    {
        public static DependencyProperty FiltersProperty = DependencyProperty.Register(
            nameof(Filters),
            typeof(EventfulCollection<FilterUnit>),
            typeof(GroupFilters),
            new PropertyMetadata(default(EventfulCollection<FilterUnit>), null));

        public static DependencyProperty ApplyFilterCommandProperty = DependencyProperty.Register(
            nameof(ApplyFilterCommand),
            typeof(ICommand),
            typeof(GroupFilters),
            new PropertyMetadata(default(ICommand), null));

        public static DependencyProperty ClearFilterCommandProperty = DependencyProperty.Register(
            nameof(ClearFilterCommand),
            typeof(ICommand),
            typeof(GroupFilters),
            new PropertyMetadata(default(ICommand), null));

        public Dictionary<string, FilterUnit> FiltersDictionary { get; }

        public ICommand ApplyFilterCommand
        {
            get => (ICommand)GetValue(ApplyFilterCommandProperty);
            set => SetValue(ApplyFilterCommandProperty, value);
        }

        public ICommand ClearFilterCommand
        {
            get => (ICommand)GetValue(ClearFilterCommandProperty);
            set => SetValue(ClearFilterCommandProperty, value);
        }

        public EventfulCollection<FilterUnit> Filters
        {
            get => (EventfulCollection<FilterUnit>)GetValue(FiltersProperty);
            set
            {
                SetValue(FiltersProperty, value);
                Filters.Action = (item) =>
                {
                    FiltersDictionary.Add(item.Name, item);
                };
            }
        }

        public GroupFilters()
        {
            this.InitializeComponent();
            FiltersDictionary = new Dictionary<string, FilterUnit>();
        }

        private void OnClearButtonClick(object sender, RoutedEventArgs e)
        {
            //TODO: need to get the default dictionary
            foreach (var key in FiltersDictionary.Keys)
            {
                FiltersDictionary[key].SetDefault();
            }
            ClearFilterCommand.Execute(FiltersDictionary);
        }

        private void OnFilterButtonClick(object sender, RoutedEventArgs e)
        {
            ApplyFilterCommand.Execute(FiltersDictionary);
        }
    }
}

using JitHub.Models;
using JitHub.Models.Filter;
using JitHub.WinUI.Views.Controls.Filter;
using System.Collections.Generic;
using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.WinUI.Views.Controls.Common
{
    public sealed partial class GroupFilters : UserControl
    {
        public static DependencyProperty FiltersProperty = DependencyProperty.Register(
            nameof(Filters),
            typeof(object),
            typeof(GroupFilters),
            new PropertyMetadata(null, OnFiltersChanged));

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

        public Dictionary<string, FilterUnit> FiltersDictionary { get; } = new();

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
            set => SetValue(FiltersProperty, value);
        }

        public GroupFilters()
        {
            this.InitializeComponent();
        }

        private static void OnFiltersChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not GroupFilters self)
            {
                return;
            }

            self.FiltersDictionary.Clear();
            if (e.NewValue is EventfulCollection<FilterUnit> filters)
            {
                foreach (var item in filters.Instance)
                {
                    self.FiltersDictionary[item.Name] = item;
                }

                filters.Action = item =>
                {
                    self.FiltersDictionary[item.Name] = item;
                };
            }

            self.Bindings.Update();
        }

        private void OnClearButtonClick(object sender, RoutedEventArgs e)
        {
            CommitFilterPresenterValues(this);
            //TODO: need to get the default dictionary
            foreach (var key in FiltersDictionary.Keys)
            {
                FiltersDictionary[key].SetDefault();
            }
            RefreshFilterPresenterValues(this);
            ClearFilterCommand.Execute(FiltersDictionary);
        }

        private void OnFilterButtonClick(object sender, RoutedEventArgs e)
        {
            CommitFilterPresenterValues(this);
            ApplyFilterCommand.Execute(FiltersDictionary);
        }

        private static void CommitFilterPresenterValues(DependencyObject parent)
        {
            int childCount = VisualTreeHelper.GetChildrenCount(parent);
            for (int index = 0; index < childCount; index++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, index);
                switch (child)
                {
                    case DropdownFilterPresenter dropdownFilterPresenter:
                        dropdownFilterPresenter.Commit();
                        break;
                    case TextFilterPresenter textFilterPresenter:
                        textFilterPresenter.Commit();
                        break;
                    case DateFilterPresenter dateFilterPresenter:
                        dateFilterPresenter.Commit();
                        break;
                }

                CommitFilterPresenterValues(child);
            }
        }

        private static void RefreshFilterPresenterValues(DependencyObject parent)
        {
            int childCount = VisualTreeHelper.GetChildrenCount(parent);
            for (int index = 0; index < childCount; index++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, index);
                switch (child)
                {
                    case DropdownFilterPresenter dropdownFilterPresenter:
                        dropdownFilterPresenter.Refresh();
                        break;
                    case TextFilterPresenter textFilterPresenter:
                        textFilterPresenter.Refresh();
                        break;
                    case DateFilterPresenter dateFilterPresenter:
                        dateFilterPresenter.Refresh();
                        break;
                }

                RefreshFilterPresenterValues(child);
            }
        }
    }
}



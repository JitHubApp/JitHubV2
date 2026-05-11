using System.Collections.ObjectModel;
using JitHub.Models.Filter;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.WinUI.Views.Controls.Filter;

public sealed partial class DropdownFilterPresenter : UserControl
{
    private bool _syncingFromModel;

    public static readonly DependencyProperty FilterProperty = DependencyProperty.Register(
        nameof(Filter),
        typeof(object),
        typeof(DropdownFilterPresenter),
        new PropertyMetadata(default(object), OnFilterChanged));

    public object? Filter
    {
        get => GetValue(FilterProperty);
        set => SetValue(FilterProperty, value);
    }

    private DropdownFilter? TypedFilter => Filter as DropdownFilter;

    public string Title => TypedFilter?.Title ?? string.Empty;

    public ObservableCollection<Selection> Selections => TypedFilter?.Selections ?? [];

    public Selection? Selected
    {
        get => TypedFilter?.Selected;
        set
        {
            if (TypedFilter is not null && value is not null && !ReferenceEquals(TypedFilter.Selected, value))
            {
                TypedFilter.Selected = value;
            }
        }
    }

    public DropdownFilterPresenter()
    {
        InitializeComponent();
    }

    private static void OnFilterChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DropdownFilterPresenter self)
        {
            self.Bindings.Update();
            self.Refresh();
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncingFromModel)
        {
            Commit();
        }
    }

    public void Commit()
    {
        if (TypedFilter is not null &&
            FilterComboBox.SelectedItem is Selection selection &&
            !ReferenceEquals(TypedFilter.Selected, selection))
        {
            TypedFilter.Selected = selection;
        }
    }

    public void Refresh()
    {
        if (TypedFilter is null)
        {
            return;
        }

        _syncingFromModel = true;
        try
        {
            FilterComboBox.SelectedItem = TypedFilter.Selected;
        }
        finally
        {
            _syncingFromModel = false;
        }
    }
}

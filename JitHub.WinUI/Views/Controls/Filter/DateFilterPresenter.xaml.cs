using System;
using JitHub.Models.Filter;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.WinUI.Views.Controls.Filter;

public sealed partial class DateFilterPresenter : UserControl
{
    private bool _syncingFromModel;

    public static readonly DependencyProperty FilterProperty = DependencyProperty.Register(
        nameof(Filter),
        typeof(object),
        typeof(DateFilterPresenter),
        new PropertyMetadata(default(object), OnFilterChanged));

    public object? Filter
    {
        get => GetValue(FilterProperty);
        set => SetValue(FilterProperty, value);
    }

    private DateFilter? TypedFilter => Filter as DateFilter;

    public string Title => TypedFilter?.Title ?? string.Empty;

    public DateTimeOffset? StartDate
    {
        get => TypedFilter?.StartDate;
        set
        {
            if (TypedFilter is not null && value is DateTimeOffset date && TypedFilter.StartDate != date)
            {
                TypedFilter.StartDate = date;
            }
        }
    }

    public string Placeholder => TypedFilter?.Placeholder ?? string.Empty;

    public DateFilterPresenter()
    {
        InitializeComponent();
    }

    private static void OnFilterChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DateFilterPresenter self)
        {
            self.Bindings.Update();
            self.Refresh();
        }
    }

    private void OnDateChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args)
    {
        if (!_syncingFromModel)
        {
            Commit();
        }
    }

    public void Commit()
    {
        if (TypedFilter is not null && FilterDatePicker.Date is DateTimeOffset date)
        {
            TypedFilter.StartDate = date;
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
            FilterDatePicker.Date = TypedFilter.StartDate;
        }
        finally
        {
            _syncingFromModel = false;
        }
    }
}

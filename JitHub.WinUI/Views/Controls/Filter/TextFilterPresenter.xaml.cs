using JitHub.Models.Filter;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.WinUI.Views.Controls.Filter;

public sealed partial class TextFilterPresenter : UserControl
{
    private bool _syncingFromModel;

    public static readonly DependencyProperty FilterProperty = DependencyProperty.Register(
        nameof(Filter),
        typeof(object),
        typeof(TextFilterPresenter),
        new PropertyMetadata(default(object), OnFilterChanged));

    public object? Filter
    {
        get => GetValue(FilterProperty);
        set => SetValue(FilterProperty, value);
    }

    private TextFilter? TypedFilter => Filter as TextFilter;

    public string Title => TypedFilter?.Title ?? string.Empty;

    public string Text
    {
        get => TypedFilter?.Text ?? string.Empty;
        set
        {
            if (TypedFilter is not null && TypedFilter.Text != value)
            {
                TypedFilter.Text = value;
            }
        }
    }

    public TextFilterPresenter()
    {
        InitializeComponent();
    }

    private static void OnFilterChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TextFilterPresenter self)
        {
            self.Bindings.Update();
            self.Refresh();
        }
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_syncingFromModel)
        {
            Commit();
        }
    }

    public void Commit()
    {
        if (TypedFilter is not null && TypedFilter.Text != FilterTextBox.Text)
        {
            TypedFilter.Text = FilterTextBox.Text;
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
            FilterTextBox.Text = TypedFilter.Text;
        }
        finally
        {
            _syncingFromModel = false;
        }
    }
}

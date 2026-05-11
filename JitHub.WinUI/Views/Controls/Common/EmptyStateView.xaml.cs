using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.WinUI.Views.Controls.Common;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class EmptyStateView : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title),
        typeof(string),
        typeof(EmptyStateView),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description),
        typeof(string),
        typeof(EmptyStateView),
        new PropertyMetadata(string.Empty));

    public EmptyStateView()
    {
        InitializeComponent();
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.WinUI.Views.Controls.App;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class AppSectionHeader : UserControl
{
    public static readonly DependencyProperty EyebrowProperty = DependencyProperty.Register(nameof(Eyebrow), typeof(string), typeof(AppSectionHeader), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(AppSectionHeader), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(nameof(Description), typeof(string), typeof(AppSectionHeader), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty ActionContentProperty = DependencyProperty.Register(nameof(ActionContent), typeof(object), typeof(AppSectionHeader), new PropertyMetadata(null));

    public AppSectionHeader()
    {
        InitializeComponent();
    }

    public string Eyebrow
    {
        get => (string)GetValue(EyebrowProperty);
        set => SetValue(EyebrowProperty, value);
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

    public object? ActionContent
    {
        get => GetValue(ActionContentProperty);
        set => SetValue(ActionContentProperty, value);
    }

    public Visibility HasEyebrow => string.IsNullOrWhiteSpace(Eyebrow) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility HasDescription => string.IsNullOrWhiteSpace(Description) ? Visibility.Collapsed : Visibility.Visible;
}

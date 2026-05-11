using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.WinUI.Views.Controls.App;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class AppSettingsCard : UserControl
{
    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(nameof(Header), typeof(string), typeof(AppSettingsCard), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(nameof(Description), typeof(string), typeof(AppSettingsCard), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty HeaderIconProperty = DependencyProperty.Register(nameof(HeaderIcon), typeof(object), typeof(AppSettingsCard), new PropertyMetadata(null));
    public static readonly DependencyProperty ActionContentProperty = DependencyProperty.Register(nameof(ActionContent), typeof(object), typeof(AppSettingsCard), new PropertyMetadata(null));

    public AppSettingsCard()
    {
        InitializeComponent();
    }

    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public object? HeaderIcon
    {
        get => GetValue(HeaderIconProperty);
        set => SetValue(HeaderIconProperty, value);
    }

    public object? ActionContent
    {
        get => GetValue(ActionContentProperty);
        set => SetValue(ActionContentProperty, value);
    }
}

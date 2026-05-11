using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.WinUI.Views.Controls.App;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class AppSidebarSection : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(AppSidebarSection), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty ActionContentProperty = DependencyProperty.Register(nameof(ActionContent), typeof(object), typeof(AppSidebarSection), new PropertyMetadata(null));
    public static readonly DependencyProperty BodyContentProperty = DependencyProperty.Register(nameof(BodyContent), typeof(object), typeof(AppSidebarSection), new PropertyMetadata(null));

    public AppSidebarSection()
    {
        InitializeComponent();
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public object? ActionContent
    {
        get => GetValue(ActionContentProperty);
        set => SetValue(ActionContentProperty, value);
    }

    public object? BodyContent
    {
        get => GetValue(BodyContentProperty);
        set => SetValue(BodyContentProperty, value);
    }
}

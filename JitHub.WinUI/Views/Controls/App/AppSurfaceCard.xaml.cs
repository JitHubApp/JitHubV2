using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.WinUI.Views.Controls.App;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class AppSurfaceCard : UserControl
{
    public static readonly DependencyProperty ChildProperty = DependencyProperty.Register(
        nameof(Child), typeof(object), typeof(AppSurfaceCard), new PropertyMetadata(null));

    public static readonly DependencyProperty CardPaddingProperty = DependencyProperty.Register(
        nameof(CardPadding), typeof(Thickness), typeof(AppSurfaceCard), new PropertyMetadata(new Thickness(16)));

    public AppSurfaceCard()
    {
        InitializeComponent();
    }

    public object? Child
    {
        get => GetValue(ChildProperty);
        set => SetValue(ChildProperty, value);
    }

    public Thickness CardPadding
    {
        get => (Thickness)GetValue(CardPaddingProperty);
        set => SetValue(CardPaddingProperty, value);
    }

    protected override AutomationPeer OnCreateAutomationPeer()
    {
        return new FrameworkElementAutomationPeer(this);
    }
}

using CommunityToolkit.Mvvm.DependencyInjection;
using JitHub.Models.Widgets;
using JitHub.Services;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace JitHub.Views.Controls.Widgets;

public sealed partial class WidgetContainer : UserControl
{
    private IWidgetService _widgetService;

    public static DependencyProperty WidgetProperty = DependencyProperty.Register(
        nameof(Widget),
        typeof(Widget),
        typeof(WidgetContainer),
        new PropertyMetadata(null, OnWidgetChange));

    private static void OnWidgetChange(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WidgetContainer self && e.NewValue != null)
        {
            self.Load();
        }
    }

    public Widget Widget
    {
        get { return (Widget)GetValue(WidgetProperty);}
        set { SetValue(WidgetProperty, value); }
    }

    public WidgetContainer()
    {
        this.InitializeComponent();
        _widgetService = Ioc.Default.GetService<IWidgetService>();
    }

    private void Load()
    {
        if (Widget.ID != null)
        {
            var widgetUI = _widgetService.Get(Widget.ID);
            Container.Children.Add(widgetUI);
        }
    }
}

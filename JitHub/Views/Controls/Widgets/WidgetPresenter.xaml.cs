using CommunityToolkit.Mvvm.DependencyInjection;
using JitHub.Models.Widgets;
using JitHub.Services;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace JitHub.Views.Controls.Widgets;

public sealed partial class WidgetPresenter : UserControl
{
    private IWidgetService _widgetService;

    public static DependencyProperty WidgetProperty = DependencyProperty.Register(
        nameof(Data),
        typeof(WidgetData),
        typeof(WidgetPresenter),
        new PropertyMetadata(null, OnWidgetChange));

    private static void OnWidgetChange(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WidgetPresenter self && e.NewValue != null)
        {
            self.Load();
        }
    }

    public WidgetData Data
    {
        get
        {
            return (WidgetData)GetValue(WidgetProperty);
        }
        set
        {
            SetValue(WidgetProperty, value);
        }
    }

    public WidgetPresenter()
    {
        this.InitializeComponent();
        _widgetService = Ioc.Default.GetService<IWidgetService>();
    }

    private void Load()
    {
        if (Data.ID != null)
        {
            var widgetUI = _widgetService.Get(Data.ID);
            Container.Children.Add(widgetUI);
        }
    }

    private void MenuFlyoutItem_Click(object sender, RoutedEventArgs e)
    {
        if (Data.Delete != null && Data.Delete.CanExecute(Data.ID))
        {
            Data.Delete.Execute(Data.ID);
        }
    }
}

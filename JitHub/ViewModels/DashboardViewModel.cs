using Microsoft.Extensions.DependencyInjection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using JitHub.Services;
using JitHub.Models.Widgets;
using System.Collections.ObjectModel;
using Windows.UI.Xaml;

namespace JitHub.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    private IWidgetService _widgetService;

    public ObservableCollection<Widget> Widgets = new ObservableCollection<Widget>();
    [ObservableProperty]
    private ICollection<string> _types;

    public DashboardViewModel()
    {
        _widgetService = Ioc.Default.GetService<IWidgetService>();
        Types = new List<string>()
        {
            WidgetType.TestOne
        };
    }

    public void Load()
    {
        Widgets.Clear();
        var widgets = _widgetService.GetAll();
        foreach ( var widget in widgets )
        {
            Widgets.Add(widget);
        }
    }

    public void AppBarButton_Click(object sender, RoutedEventArgs e)
    {
        Widgets.Add(_widgetService.Create(WidgetType.TestOne));
    }
}

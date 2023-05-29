using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using System.Collections.Generic;
using JitHub.Services;
using JitHub.Models.Widgets;
using System.Collections.ObjectModel;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using CommunityToolkit.Mvvm.Input;
using System.ComponentModel;
using System.Xml.Linq;

namespace JitHub.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    private IWidgetService _widgetService;
    private MenuFlyout _container;

    public ObservableCollection<Widget> Widgets = new ObservableCollection<Widget>();

    public DashboardViewModel()
    {
        _widgetService = Ioc.Default.GetService<IWidgetService>();
    }

    public void Initialize(MenuFlyout container)
    {
        _container = container;

        LoadInstalledWidgets();
        LoadWidgetLists();
    }

    private void LoadInstalledWidgets()
    {
        Widgets.Clear();
        var widgets = _widgetService.GetAll();

        foreach (var widget in widgets)
        {
            widget.Delete = new RelayCommand<string>(DeleteWidget);
            Widgets.Add(widget);
        }
    }

    private void LoadWidgetLists()
    {
        var widgetRegs = _widgetService.GetAllRegs();
        foreach (var widgetReg in widgetRegs)
        {
            var menuItem = new MenuFlyoutItem()
            {
                Text = widgetReg.GetName(),
                Command = new RelayCommand<WidgetBase>(AddWidget),
                CommandParameter = widgetReg
            };
            _container.Items.Add(menuItem);
        }
    }

    public void AddWidget(WidgetBase widgetBase)
    {
        Widgets.Add(_widgetService.Create(widgetBase.Type));
    }

    private void DeleteWidget(string id)
    {
        _widgetService.Delete(id);
        LoadInstalledWidgets();
    }
}

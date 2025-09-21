using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using JitHub.Models.Widgets;
using JitHub.Services;
using System.Collections.ObjectModel;
using Windows.UI.Xaml.Controls;

namespace JitHub.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    private IWidgetService _widgetService;
    private MenuFlyout _container;

    public ObservableCollection<WidgetData> Widgets = new ObservableCollection<WidgetData>();

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

    public void WidgetsReorderRequested(object sender, Widget.WidgetLayout.WidgetReorderEventArgs e)
    {
        if (e.OldIndex == e.NewIndex || e.OldIndex < 0 || e.NewIndex < 0 || e.OldIndex >= Widgets.Count || e.NewIndex >= Widgets.Count)
        {
            return;
        }
        var item = Widgets[e.OldIndex];
        Widgets.RemoveAt(e.OldIndex);
        Widgets.Insert(e.NewIndex, item);
    }
}

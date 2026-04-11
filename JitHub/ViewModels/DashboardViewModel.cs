using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using JitHub.Events;
using JitHub.Models.Widgets;
using JitHub.Services;
using System.Collections.ObjectModel;

namespace JitHub.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    private IWidgetService _widgetService;

    [ObservableProperty]
    private bool _isWidgetEditMode;

    public ObservableCollection<WidgetData> Widgets = new ObservableCollection<WidgetData>();

    public DashboardViewModel()
    {
        _widgetService = Ioc.Default.GetService<IWidgetService>();
        WeakReferenceMessenger.Default.Register<WidgetEditEvent>(this, (obj, evt) =>
        {
            IsWidgetEditMode = evt.Value;
        });
        WeakReferenceMessenger.Default.Register<WidgetCreationEvent>(this, (obj, evt) =>
        {
            Widgets.Add(evt.Value);
        });
    }

    public void Initialize()
    {
        LoadInstalledWidgets();
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
        Widgets.Move(e.OldIndex, e.NewIndex); // single move notification
    }
}

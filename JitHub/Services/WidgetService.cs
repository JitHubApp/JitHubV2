using JitHub.Helpers;
using JitHub.Models.Widgets;
using Microsoft.Toolkit.Uwp.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using Windows.UI.Xaml;

namespace JitHub.Services;

public class WidgetService : IWidgetService
{
    private const string WIDGET_CACHE_KEY = "WIDGET_CACHE_KEY";
    private Dictionary<string, WidgetBase> _widgetRegs = new Dictionary<string, WidgetBase>();
    private Dictionary<string, Widget> _widgetCache = new Dictionary<string, Widget>(); 
    private ApplicationDataStorageHelper _storage;

    public WidgetService(ISettingService settingService)
    {
        var serializer = new WidgetSerializer();
        _storage = ApplicationDataStorageHelper.GetCurrent(serializer);
    }

    // create and save to local storage
    public Widget Create(string type)
    {
        var success = _widgetRegs.TryGetValue(type, out var widgetReg);
        if (success)
        {
            var widget = widgetReg.Create();
            _widgetCache.Add(widget.ID, widget);
            _storage.Save(WIDGET_CACHE_KEY, _widgetCache);
            return widget;
        }
        throw new Exception($"No such widget found: {type}");
    }

    public UIElement Get(string id)
    {
        var success = _widgetCache.TryGetValue(id, out var widget);
        if (!success)
        {
            throw new Exception($"No widget found: {id}");
        }
        success = _widgetRegs.TryGetValue(widget.Type, out var widgetReg);
        if (!success)
        {
            throw new Exception($"No widget regidtered: {widget.Type}");
        }
        return widgetReg.GetElement(id);
    }

    public ICollection<Widget> GetAll()
    {
        return _widgetCache.Values.ToList();
    }

    public void Initialize()
    {
        var cache = _storage.Read<Dictionary<string, Widget>>(WIDGET_CACHE_KEY);
        if (cache != null)
        {
            foreach (var (key, value) in cache)
            {
                _widgetCache.Add(key, value);
            }
        }
    }

    void IWidgetService.Register(WidgetBase widget)
    {
        _widgetRegs.Add(widget.Type, widget);
    }
}

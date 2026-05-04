using System.Windows.Input;
using JitHub.Models.NavArgs;
using Microsoft.UI.Xaml;

namespace JitHub.Services;

public class ModalService
{
    private ICommand? _open;
    private ICommand? _close;
    private ICommand? _callback;
    private bool _opened;
    private bool _initialized;

    public void Init(ICommand open, ICommand close)
    {
        _open = open;
        _close = close;
        _initialized = true;
    }

    public void Open(string title, FrameworkElement element)
    {
        Open(title, element, false);
    }

    public void Open(string title, FrameworkElement element, bool useHeader)
    {
        var arg = new ModalArg
        {
            Title = title,
            Content = element,
            UseHeader = useHeader
        };

        if (_initialized && _open?.CanExecute(arg) == true && !_opened)
        {
            _open.Execute(arg);
            _opened = true;
        }
    }

    public void Open(FrameworkElement element)
    {
        var arg = new ModalArg
        {
            Content = element,
            UseHeader = true
        };

        if (_initialized && _open?.CanExecute(arg) == true && !_opened)
        {
            _open.Execute(arg);
            _opened = true;
        }
    }

    public void Open(string title, FrameworkElement element, ICommand callback)
    {
        _callback = callback;
        Open(title, element);
    }

    public void Close()
    {
        if (_initialized && _close?.CanExecute(null) == true && _opened)
        {
            _close.Execute(null);
            _opened = false;

            if (_callback?.CanExecute(null) == true)
            {
                _callback.Execute(null);
                _callback = null;
            }
        }
    }
}

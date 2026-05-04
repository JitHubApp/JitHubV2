using System;
using System.Windows.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace JitHub.Services;

public class NavigationService
{
    private readonly Frame _rootFrame;
    private ICommand? _changeTabTitleCommand;

    public NavigationService(Frame frame)
    {
        _rootFrame = frame;
        ApplicationFrame = frame;
        RepoFrame = frame;
    }

    public Frame RepoFrame { get; set; }

    public Frame? ApplicationFrame { get; set; }

    public Type? UnauthorizedPage { get; set; }

    public Type? RootHomePage { get; set; }

    public Type? ShellHomePage { get; set; }

    public void RegisterTabTitleChangeEvent(ICommand command)
    {
        _changeTabTitleCommand = command;
    }

    public void Unauthorized()
    {
        ApplicationFrame = _rootFrame;
        RepoFrame = _rootFrame;

        if (UnauthorizedPage is not null)
        {
            _rootFrame.Navigate(UnauthorizedPage, null, new SuppressNavigationTransitionInfo());
        }
    }

    public void NavigateTo(string title, Type page)
    {
        (ApplicationFrame ?? _rootFrame).Navigate(page, null, new SuppressNavigationTransitionInfo());
        ChangeTabTitle(title);
    }

    public void NavigateTo(string title, Type page, object? parameter)
    {
        (ApplicationFrame ?? _rootFrame).Navigate(page, parameter, new SuppressNavigationTransitionInfo());
        ChangeTabTitle(title);
    }

    public void GoHome()
    {
        if (ApplicationFrame is not null && ApplicationFrame != _rootFrame && ShellHomePage is not null)
        {
            ApplicationFrame.Navigate(ShellHomePage, null, new SuppressNavigationTransitionInfo());
            ChangeTabTitle("Home");
            return;
        }

        GoRootHome();
    }

    public void GoRootHome()
    {
        if (RootHomePage is not null)
        {
            _rootFrame.Navigate(RootHomePage, null, new SuppressNavigationTransitionInfo());
        }
    }

    public void RepoNagivateTo(Type page)
    {
        RepoFrame.Navigate(page, null, new SuppressNavigationTransitionInfo());
    }

    public void RepoNagivateTo(Type page, object? parameter)
    {
        RepoFrame.Navigate(page, parameter, new SuppressNavigationTransitionInfo());
    }

    public void ChangeTabTitle(string name)
    {
        if (_changeTabTitleCommand?.CanExecute(name) == true)
        {
            _changeTabTitleCommand.Execute(name);
        }
    }
}

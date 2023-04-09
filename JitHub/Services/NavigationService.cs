using JitHub.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Windows.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace JitHub.Services
{
    public class NavigationService : ObservableObject
    {
        private Frame _rootFrame;
        private Frame _applicationFrame;
        private Frame _repoFrame;
        private ICommand _changeTabTitleCommand;
        public Frame RepoFrame { get => _repoFrame; set => SetProperty(ref _repoFrame, value); }
        public Frame ApplicationFrame { get => _applicationFrame; set => SetProperty(ref _applicationFrame, value); }
        public NavigationService(Frame frame)
        {
            _rootFrame = frame;
        }

        public void RegisterTabTitleChangeEvent(ICommand command)
        {
            if (_changeTabTitleCommand == null)
            {
                _changeTabTitleCommand = command;
            }
        }

        public void Unauthorized()
        {
            _rootFrame.Navigate(typeof(LoginPage), null, new SuppressNavigationTransitionInfo());
        }

        public void NavigateTo(string title, Type page)
        {
            ApplicationFrame.Navigate(page);
            if (_changeTabTitleCommand != null && _changeTabTitleCommand.CanExecute(title))
            {
                _changeTabTitleCommand.Execute(title);
            }
        }

        public void NavigateTo(string title, Type page, object parameter)
        {
            ApplicationFrame.Navigate(page, parameter);
            if (_changeTabTitleCommand != null && _changeTabTitleCommand.CanExecute(title))
            {
                _changeTabTitleCommand.Execute(title);
            }
        }

        public void GoHome()
        {
            ApplicationFrame.Navigate(typeof(DashboardPage), null, new SuppressNavigationTransitionInfo());
            if (_changeTabTitleCommand != null && _changeTabTitleCommand.CanExecute("Home"))
            {
                _changeTabTitleCommand.Execute("Home");
            }
        }

        public void RepoNagivateTo(Type page)
        {
            RepoFrame.Navigate(page);
        }

        public void RepoNagivateTo(Type page, object parameter)
        {
            RepoFrame.Navigate(page, parameter);
        }

        public void ChangeTabTitle(string name)
        {
            if (_changeTabTitleCommand != null && _changeTabTitleCommand.CanExecute(name))
            {
                _changeTabTitleCommand.Execute(name);
            }
        }
    }
}

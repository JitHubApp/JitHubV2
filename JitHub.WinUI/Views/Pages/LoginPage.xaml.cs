using System;
using JitHub.Services;
using JitHub.WinUI.ViewModels.Pages;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.WinUI.Views.Pages;

public sealed partial class LoginPage : Page
{
    private readonly NavigationService _navigationService;
    public LoginPageViewModel ViewModel { get; }

    public LoginPage()
    {
        ViewModel = ((App)Application.Current).GetService<LoginPageViewModel>();
        _navigationService = ((App)Application.Current).GetService<NavigationService>();
        InitializeComponent();
        DataContext = ViewModel;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsAuthenticated)
        {
            _navigationService.GoHome();
            return;
        }

        ViewModel.PrepareForDisplay();
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.StartLoginAsync();
    }
}

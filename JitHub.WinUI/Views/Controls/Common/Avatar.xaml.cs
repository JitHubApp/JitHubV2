using System;
using CommunityToolkit.Mvvm.DependencyInjection;
using JitHub.Models;
using JitHub.Services;
using JitHub.WinUI.ViewModels.Pages;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.WinUI.Views.Controls.Common
{
    public sealed partial class Avatar : UserControl
    {
        public static DependencyProperty SizeProperty = DependencyProperty.Register(
            "Size",
            typeof(UISize),
            typeof(Avatar),
            new PropertyMetadata(default(UISize), OnBindablePropertyChanged));
        public static DependencyProperty UrlProperty = DependencyProperty.Register(
            "Url",
            typeof(string),
            typeof(Avatar),
            new PropertyMetadata(default(string), OnUrlChanged));
        public static DependencyProperty LoginProperty = DependencyProperty.Register(
            "Login",
            typeof(string),
            typeof(Avatar),
            new PropertyMetadata(default(string), OnBindablePropertyChanged));
        public static readonly DependencyProperty DisplayNameProperty = DependencyProperty.Register(
            nameof(DisplayName),
            typeof(string),
            typeof(Avatar),
            new PropertyMetadata(default(string), OnBindablePropertyChanged));
        public static readonly DependencyProperty AutomationInstanceIdProperty = DependencyProperty.Register(
            nameof(AutomationInstanceId),
            typeof(string),
            typeof(Avatar),
            new PropertyMetadata(default(string), OnBindablePropertyChanged));
        public static DependencyProperty ShowLoginProperty = DependencyProperty.Register(
            "ShowLogin",
            typeof(bool),
            typeof(Avatar),
            new PropertyMetadata(default(bool), OnBindablePropertyChanged));
        public static readonly DependencyProperty IsProfileNavigationEnabledProperty = DependencyProperty.Register(
            nameof(IsProfileNavigationEnabled),
            typeof(bool),
            typeof(Avatar),
            new PropertyMetadata(true, OnBindablePropertyChanged));
        public static readonly DependencyProperty NavigationSourceProperty = DependencyProperty.Register(
            nameof(NavigationSource),
            typeof(string),
            typeof(Avatar),
            new PropertyMetadata("avatar", OnBindablePropertyChanged));

        private static void OnUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is Avatar self)
            {
                self.Bindings.Update();
            }
        }

        private static void OnBindablePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is Avatar self)
            {
                self.Bindings.Update();
            }
        }


        public UISize Size
        {
            get => (UISize)GetValue(SizeProperty);
            set
            {
                SetValue(SizeProperty, value);
            }
        }
        public string? Url
        {
            get => (string?)GetValue(UrlProperty);
            set
            {
                SetValue(UrlProperty, value);
            }
        }

        public string? Login
        {
            get => (string?)GetValue(LoginProperty);
            set
            {
                SetValue(LoginProperty, value);
            }
        }

        public bool ShowLogin
        {
            get => (bool)GetValue(ShowLoginProperty);
            set
            {
                SetValue(ShowLoginProperty, value);
            }
        }

        public string? DisplayName
        {
            get => (string?)GetValue(DisplayNameProperty);
            set => SetValue(DisplayNameProperty, value);
        }

        public string? AutomationInstanceId
        {
            get => (string?)GetValue(AutomationInstanceIdProperty);
            set => SetValue(AutomationInstanceIdProperty, value);
        }

        public bool IsProfileNavigationEnabled
        {
            get => (bool)GetValue(IsProfileNavigationEnabledProperty);
            set => SetValue(IsProfileNavigationEnabledProperty, value);
        }

        public string NavigationSource
        {
            get => (string)GetValue(NavigationSourceProperty);
            set => SetValue(NavigationSourceProperty, value);
        }

        public bool IsProfileAvailable =>
            IsProfileNavigationEnabled && UserIdentityNavigationPolicy.CanNavigate(Login);

        public string DisplayText => !string.IsNullOrWhiteSpace(DisplayName)
            ? DisplayName!.Trim()
            : Login?.Trim() ?? string.Empty;

        public string ProfileAccessibleName => IsProfileAvailable
            ? $"Open @{Login!.Trim()} profile"
            : "Profile unavailable";

        public string ProfileToolTip => ProfileAccessibleName;

        public string ProfileAutomationId => UserIdentityAutomationId.Create(
            NavigationSource,
            AutomationInstanceId,
            Login);

        public Avatar()
        {
            this.InitializeComponent();
            ProfileButton.PointerEntered += ProfileButton_PointerEntered;
            ProfileButton.PointerExited += ProfileButton_PointerExited;
            ProfileButton.PointerPressed += ProfileButton_PointerPressed;
            ProfileButton.PointerReleased += ProfileButton_PointerReleased;
        }

        private void ProfileButton_Click(object sender, RoutedEventArgs e)
            => OpenProfile();

        private void ProfileButton_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key != Windows.System.VirtualKey.Enter)
                return;

            e.Handled = true;
            OpenProfile();
        }

        private void ProfileButton_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (IsProfileAvailable)
            {
                HoverRing.Opacity = 1;
                ProfileButton.Opacity = 1;
            }
        }

        private void ProfileButton_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            HoverRing.Opacity = 0;
            ProfileButton.Opacity = 1;
        }

        private void ProfileButton_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (IsProfileAvailable)
                ProfileButton.Opacity = 0.72;
        }

        private void ProfileButton_PointerReleased(object sender, PointerRoutedEventArgs e) =>
            ProfileButton.Opacity = 1;

        private void OpenProfile()
        {
            string login = Login?.Trim() ?? string.Empty;
            if (!IsProfileNavigationEnabled || !UserIdentityNavigationPolicy.CanNavigate(login))
                return;

            string source = string.IsNullOrWhiteSpace(NavigationSource)
                ? "avatar"
                : NavigationSource.Trim();
            Ioc.Default.GetService<ShellPageViewModel>()?.OpenUserProfile(
                login,
                source,
                ProfileAutomationId);
        }
    }
}



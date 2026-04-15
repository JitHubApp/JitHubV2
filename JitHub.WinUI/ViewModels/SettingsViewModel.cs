using JitHub.Models;
using JitHub.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.WinUI.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using Windows.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.AppLifecycle;
using JitHub.Models.NavArgs;
using JitHub.WinUI.Views.Pages;

namespace JitHub.WinUI.ViewModels
{
    public class SettingsViewModel : ObservableObject
    {
        private ICollection<string> _themes = [];
        private string _selectedTheme = ThemeConst.System;
        private readonly IThemeService _themeService;
        private readonly ISettingService _settingService;
        private readonly NavigationService _navigationService;
        private readonly IGitHubService _githubService;
        private bool _restartRequired;
        private string _version = string.Empty;
        private int _clickedTime;

        public bool RestartRequired
        {
            get => _restartRequired;
            set => SetProperty(ref _restartRequired, value);
        }
        public ICollection<string> Themes
        {
            get => _themes;
            set => SetProperty(ref _themes, value);
        }
        public string SelectedTheme
        {
            get => _selectedTheme;
            set => SetProperty(ref _selectedTheme, value);
        }
        public string Version
        {
            get => _version;
            set => SetProperty(ref _version, value);
        }
        public int ClickedTime
        {
            get => _clickedTime;
            set => SetProperty(ref _clickedTime, value);
        }
        public GlobalViewModel GlobalViewModel { get; }
        public CreditPersonale Nero { get; }
        public CreditPersonale Get { get; }
        public CreditPersonale Keira { get; }
        public CreditPersonale Jakub { get; }
        public CreditPersonale ZyC { get; }
        public SettingsViewModel()
        {
            _themeService = Ioc.Default.GetService<IThemeService>()
                ?? throw new InvalidOperationException("IThemeService is not registered.");
            _settingService = Ioc.Default.GetService<ISettingService>()
                ?? throw new InvalidOperationException("ISettingService is not registered.");
            _navigationService = Ioc.Default.GetService<NavigationService>()
                ?? throw new InvalidOperationException("NavigationService is not registered.");
            _githubService = Ioc.Default.GetService<IGitHubService>()
                ?? throw new InvalidOperationException("IGitHubService is not registered.");
            GlobalViewModel = Ioc.Default.GetService<GlobalViewModel>()
                ?? throw new InvalidOperationException("GlobalViewModel is not registered.");
            Version = $"{SystemInformation.Instance.ApplicationVersion.Major}.{SystemInformation.Instance.ApplicationVersion.Minor}.{SystemInformation.Instance.ApplicationVersion.Build}.{SystemInformation.Instance.ApplicationVersion.Revision}";
            var light = ThemeConst.Light;
            var dark = ThemeConst.Dark;
            var system = ThemeConst.System;
            var currentTheme = _themeService.GetTheme();
            if (currentTheme == ThemeConst.Dark)
            {
                SelectedTheme = dark;
            }
            else if (currentTheme == ThemeConst.Light)
            {
                SelectedTheme = light;
            }
            else
            {
                SelectedTheme = system;
            }
            Themes = new List<string>
            {
                light,
                dark,
                system
            };
            Nero = new CreditPersonale(
                "ms-appx:///Assets/ContributorsProfilePhotos/NeroProfile.jpg",
                "Nero Cui",
                "Developer",
                "I'm a software engineer working at Microsoft. I like developing apps, playing video games and sharing my knowledge. JitHub is a personal project of mine, but I have plans to add more and more feature to it.",
                Color.FromArgb(255, 148, 136, 138),
                new List<PersonalLink>()
                {
                    new PersonalLink("https://www.linkedin.com/in/zhuowen-nero-cui-7a3ba8116/", PersonalLink.LinkedInLogo),
                    new PersonalLink("https://twitter.com/zhuowencui", PersonalLink.TwitterLogo),
                }
            );
            Get = new CreditPersonale(
                "ms-appx:///Assets/ContributorsProfilePhotos/GetProfile.png",
                "Get",
                "Developer",
                "I'm a hobbyist app developer. I like developing apps that would either become a proof of concept that push boundaries of what is already possible or the productivity app that I would personally use myself.",
                Color.FromArgb(255, 148, 136, 138),
                new List<PersonalLink>()
                {
                    new PersonalLink("https://github.com/Get0457", PersonalLink.GitHubLogo),
                }
            );
            Keira = new CreditPersonale(
                "ms-appx:///Assets/ContributorsProfilePhotos/KeiraProfile.png",
                "Keira Xu",
                "Logo Designer",
                "Keira is a Product Designer at Microsoft, ex-EA, with a passion for interaction UI/UX design, prototyping and video creation. She received the 2017 Red Dot Award for her innovative designs.",
                Color.FromArgb(255, 148, 112, 100),
                new List<PersonalLink>()
                {
                    new PersonalLink("https://www.linkedin.com/in/kejiaxu/", PersonalLink.LinkedInLogo),
                }
            );
            Jakub = new CreditPersonale(
                "ms-appx:///Assets/ContributorsProfilePhotos/JakubProfile.png",
                "Jakub Bugajski",
                "UI Designer",
                "Jakub is a 13 year old UI/UX designer from Poland. He got featured on The Verge and many other sites for his File Explorer design.",
                Color.FromArgb(255, 247, 205, 185),
                new List<PersonalLink>()
                {
                    new PersonalLink("https://twitter.com/AlurDesign", PersonalLink.TwitterLogo),
                }
            );
            ZyC = new CreditPersonale(
                "",
                "Ze Chen",
                "Developer",
                "Software engineer, always trying to learn something new :)",
                Color.FromArgb(255, 148, 136, 138),
                new List<PersonalLink>()
                {
                    new PersonalLink("https://github.com/billzyc", PersonalLink.GitHubLogo),
                }
            );
        }

        public void Restart()
        {
            AppInstance.Restart(SelectedTheme);
        }

        public async void ViewJitHubCode()
        {
            var jithub = await _githubService.GetRepository("JitHubApp", "JitHubV2");
            if (jithub is not null)
            {
                _navigationService.NavigateTo("JitHub", typeof(RepoDetailPage), new RepoDetailPageArgs(RepoPageType.CodePage, jithub));
            }
        }

        public void SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var currentTheme = _themeService.GetTheme();
            if (e.AddedItems.OfType<string>().FirstOrDefault() is string added &&
                e.RemovedItems.OfType<string>().FirstOrDefault() is string removed &&
                added == removed &&
                added == currentTheme)
            {
                RestartRequired = false;
                return;
            }

            _themeService.SetTheme(SelectedTheme);
            if (SelectedTheme == ThemeConst.System)
            {
                RestartRequired = currentTheme != SelectedTheme;
            }
            else
            {
                var currentApplicationTheme = App.Current.RequestedTheme;
                if (SelectedTheme == ThemeConst.Light)
                {
                    RestartRequired = currentApplicationTheme == ApplicationTheme.Dark;
                }
                else
                {
                    RestartRequired = currentApplicationTheme == ApplicationTheme.Light;
                }
            }
        }
    }
}





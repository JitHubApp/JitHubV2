using JitHub.Models;
using JitHub.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Toolkit.Uwp.Helpers;
using System;
using System.Collections.Generic;
using Windows.ApplicationModel.Core;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using JitHub.Models.NavArgs;
using JitHub.Views.Pages;

namespace JitHub.ViewModels
{
    public class SettingsViewModel : ObservableObject
    {
        private ICollection<string> _themes;
        private string _selectedTheme;
        private IThemeService _themeService;
        private ISettingService _settingService;
        private NavigationService _navigationService;
        private IGitHubService _githubService;
        private bool _restartRequired;
        private string _version;
        private int _clickedTime = 0;

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
        public CreditPersonale Nero;
        public CreditPersonale Get;
        public CreditPersonale Keira;
        public CreditPersonale Jakub;
        public SettingsViewModel()
        {
            _themeService = Ioc.Default.GetService<IThemeService>();
            _settingService = Ioc.Default.GetService<ISettingService>();
            _navigationService = Ioc.Default.GetService<NavigationService>();
            _githubService = Ioc.Default.GetService<IGitHubService>();
            GlobalViewModel = Ioc.Default.GetService<GlobalViewModel>();
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
        }

        public async void Restart()
        {
            await CoreApplication.RequestRestartAsync(SelectedTheme);
        }

        public async void ViewJitHubCode()
        {
            var jithub = await _githubService.GetRepository("nerocui", "JitHubV2");
            _navigationService.NavigateTo("JitHub", typeof(RepoDetailPage), new RepoDetailPageArgs(RepoPageType.CodePage, jithub));
        }

        public void SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var currentTheme = _themeService.GetTheme();
            try
            {
                var added = (string)e.AddedItems[0];
                var removed = (string)e.RemovedItems[0];
                if (added == removed && added == currentTheme)
                {
                    RestartRequired = false;
                    return;
                }
            }
            catch
            {
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

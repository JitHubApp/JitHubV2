using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using JitHub.Models;
using JitHub.Services;
using JitHub.Services.Layout;
using JitHub.WinUI.Helpers;
using JitHub.WinUI.ViewModels.Pages;
using JitHub.WinUI.Views.Dialogs;
using JitHubModels = JitHub.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;
using WinRT.Interop;

namespace JitHub.WinUI.Views.Pages;

public sealed partial class SettingsPage : Page
{
    private readonly SettingsActionGate _actionGate = new();
    private bool _isSynchronizingThemeCards;

    public SettingsPage()
    {
        ViewModel = ((App)Application.Current).GetService<SettingsPageViewModel>();
        InitializeComponent();
        RegisterThemeCardKeyboardNavigation();
        DataContext = ViewModel;
        _actionGate.StateChanged += SettingsActionGate_StateChanged;
        Loaded += SettingsPage_Loaded;
    }

    public SettingsPageViewModel ViewModel { get; }

    private void RegisterThemeCardKeyboardNavigation()
    {
        Microsoft.UI.Xaml.Input.KeyEventHandler handler = ThemeButton_KeyDown;
        SystemThemeButton.AddHandler(UIElement.KeyDownEvent, handler, handledEventsToo: true);
        LightThemeButton.AddHandler(UIElement.KeyDownEvent, handler, handledEventsToo: true);
        DarkThemeButton.AddHandler(UIElement.KeyDownEvent, handler, handledEventsToo: true);
    }

    public IReadOnlyList<CreditPersonale> Developers { get; } =
    [
        new(
            "ms-appx:///Assets/ContributorsProfilePhotos/NeroProfile.jpg",
            "Nero Cui",
            "Developer",
            "I'm a software engineer working at Microsoft. I like developing apps, playing video games and sharing my knowledge. JitHub is a personal project of mine, but I have plans to add more and more feature to it.",
            Color.FromArgb(255, 148, 136, 138),
            [
                new PersonalLink("https://www.linkedin.com/in/zhuowen-nero-cui-7a3ba8116/", PersonalLink.LinkedInLogo),
                new PersonalLink("https://twitter.com/zhuowencui", PersonalLink.TwitterLogo)
            ]),
        new(
            "ms-appx:///Assets/ContributorsProfilePhotos/GetProfile.png",
            "Get",
            "Developer",
            "I'm a hobbyist app developer. I like developing apps that would either become a proof of concept that push boundaries of what is already possible or the productivity app that I would personally use myself.",
            Color.FromArgb(255, 148, 136, 138),
            [
                new PersonalLink("https://github.com/Get0457", PersonalLink.GitHubLogo)
            ]),
        new(
            "",
            "Ze Chen",
            "Developer",
            "Software engineer, always trying to learn something new :)",
            Color.FromArgb(255, 148, 136, 138),
            [
                new PersonalLink("https://github.com/billzyc", PersonalLink.GitHubLogo)
            ]),
        new(
            "ms-appx:///Assets/ContributorsProfilePhotos/XueyangProfile.png",
            "Xueyang Song",
            "ML + Battery Researcher",
            "Xueyang is an ML and battery researcher whose publications are widely referenced across academic institutions and industry, connecting data-driven methods with practical energy research.",
            Color.FromArgb(255, 176, 161, 132),
            [
                new PersonalLink("https://www.linkedin.com/in/xueyang-song-b79bb9192/", PersonalLink.LinkedInLogo),
                new PersonalLink("https://scholar.google.com/citations?user=4FvfgxkAAAAJ&hl=en", PersonalLink.GoogleScholarLogo)
            ])
    ];

    public IReadOnlyList<CreditPersonale> Designers { get; } =
    [
        new(
            "ms-appx:///Assets/ContributorsProfilePhotos/KeiraProfile.png",
            "Keira Xu",
            "Logo Designer",
            "Keira is a Product Designer at Microsoft, ex-EA, with a passion for interaction UI/UX design, prototyping and video creation. She received the 2017 Red Dot Award for her innovative designs.",
            Color.FromArgb(255, 148, 112, 100),
            [
                new PersonalLink("https://www.linkedin.com/in/kejiaxu/", PersonalLink.LinkedInLogo)
            ]),
        new(
            "ms-appx:///Assets/ContributorsProfilePhotos/JakubProfile.png",
            "Jakub Bugajski",
            "UI Designer",
            "Jakub is a 13 year old UI/UX designer from Poland. He got featured on The Verge and many other sites for his File Explorer design.",
            Color.FromArgb(255, 247, 205, 185),
            [
                new PersonalLink("https://twitter.com/AlurDesign", PersonalLink.TwitterLogo)
            ])
    ];

    private async void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= SettingsPage_Loaded;
        UpdateResponsiveState(ActualWidth);
        try
        {
            await ViewModel.InitializeAsync();
            SynchronizeThemeCards();
        }
        catch (Exception ex)
        {
            ViewModel.ReportActionFailure(ex);
        }

        // Settings sections are local, committed content even when a diagnostics refresh fails.
        ProductPerformanceReadiness.CommitRoute(
            "settings",
            ProductPerformanceReadiness.CountIdentity(ViewModel.SettingsSections.Count));
    }

    private void SettingsPage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateResponsiveState(e.NewSize.Width);
    }

    private void SettingsSectionList_ContainerContentChanging(
        ListViewBase sender,
        ContainerContentChangingEventArgs args)
    {
        if (args.Item is not SettingsSectionItem section)
        {
            return;
        }

        AutomationProperties.SetAutomationId(args.ItemContainer, section.AutomationId);
        AutomationProperties.SetName(args.ItemContainer, section.Title);
    }

    private void CompactSectionPicker_DropDownOpened(object? sender, object e)
    {
        _ = DispatcherQueue.TryEnqueue(ApplyCompactSectionAutomationProperties);
    }

    private void SettingsSectionSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            SettingsContentScrollViewer.UpdateLayout();
            SettingsContentScrollViewer.ChangeView(null, 0, null, disableAnimation: true);
        });
    }

    private void ApplyCompactSectionAutomationProperties()
    {
        foreach (SettingsSectionItem section in ViewModel.SettingsSections)
        {
            if (CompactSectionPicker.ContainerFromItem(section) is ComboBoxItem container)
            {
                AutomationProperties.SetAutomationId(container, $"{section.AutomationId}_Compact");
                AutomationProperties.SetName(container, section.Title);
            }
        }
    }

    private void UpdateResponsiveState(double availableWidth)
    {
        WorkspaceChromeState chrome = WorkspaceChromeLayout.Calculate(availableWidth);
        SettingsWorkspaceLayoutState layout = SettingsWorkspaceLayout.Calculate(availableWidth);
        string state = layout.Mode switch
        {
            SettingsWorkspaceMode.Narrow => "NarrowSettingsState",
            SettingsWorkspaceMode.Compact => "CompactSettingsState",
            _ => "WideSettingsState"
        };
        VisualStateManager.GoToState(this, state, useTransitions: false);
        SettingsRootGrid.Padding = chrome.Insets.ToThickness();
    }

    private void SystemThemeButton_Checked(object sender, RoutedEventArgs e)
    {
        if (!_isSynchronizingThemeCards && SystemThemeButton.IsChecked == true)
        {
            ViewModel.SelectTheme(JitHubModels.ThemeConst.System);
        }
    }

    private void LightThemeButton_Checked(object sender, RoutedEventArgs e)
    {
        if (!_isSynchronizingThemeCards && LightThemeButton.IsChecked == true)
        {
            ViewModel.SelectTheme(JitHubModels.ThemeConst.Light);
        }
    }

    private void DarkThemeButton_Checked(object sender, RoutedEventArgs e)
    {
        if (!_isSynchronizingThemeCards && DarkThemeButton.IsChecked == true)
        {
            ViewModel.SelectTheme(JitHubModels.ThemeConst.Dark);
        }
    }

    private void ThemeButton_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        RadioButton[] cards = [SystemThemeButton, LightThemeButton, DarkThemeButton];
        int currentIndex = Array.IndexOf(cards, sender as RadioButton);
        int direction = e.Key switch
        {
            VirtualKey.Left or VirtualKey.Up => -1,
            VirtualKey.Right or VirtualKey.Down => 1,
            _ => 0
        };
        if (currentIndex < 0 || direction == 0)
        {
            return;
        }

        int nextIndex = (currentIndex + direction + cards.Length) % cards.Length;
        cards[nextIndex].IsChecked = true;
        _ = cards[nextIndex].Focus(FocusState.Keyboard);
        e.Handled = true;
    }

    private async void RetryDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.RefreshDiagnosticsAsync();
        }
        catch (Exception ex)
        {
            ViewModel.ReportActionFailure(ex);
        }
    }

    private void SynchronizeThemeCards()
    {
        _isSynchronizingThemeCards = true;
        try
        {
            SystemThemeButton.IsChecked = ViewModel.IsSystemThemeSelected;
            LightThemeButton.IsChecked = ViewModel.IsLightThemeSelected;
            DarkThemeButton.IsChecked = ViewModel.IsDarkThemeSelected;
        }
        finally
        {
            _isSynchronizingThemeCards = false;
        }
    }

    private async void ClearDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        await RunExclusiveUiActionAsync(sender as Control, async () =>
        {
            if (await ConfirmAsync(
                    L("Settings/Dialogs/ClearDiagnostics/Title", "Clear diagnostics?"),
                    L("Settings/Dialogs/ClearDiagnostics/Body", "This clears the local diagnostics NDJSON file. It does not change telemetry settings or cached GitHub data."),
                    L("Settings/Dialogs/ClearDiagnostics/Primary", "Clear diagnostics"),
                    "SettingsConfirmClearDiagnostics"))
            {
                await ViewModel.ClearDiagnosticsAsync();
            }
        });
    }

    private async void ClearQueryCacheButton_Click(object sender, RoutedEventArgs e)
    {
        await RunExclusiveUiActionAsync(sender as Control, async () =>
        {
            if (await ConfirmAsync(
                    L("Settings/Dialogs/ClearQueryCache/Title", "Clear GitHub query cache?"),
                    L("Settings/Dialogs/ClearQueryCache/Body", "This clears cached GitHub query metadata and JSON/blob/diff payload files. It does not clear avatar images or diagnostics."),
                    L("Settings/Dialogs/ClearQueryCache/Primary", "Clear query cache"),
                    "SettingsConfirmClearQueryCache"))
            {
                await ViewModel.ClearQueryCacheAsync();
            }
        });
    }

    private async void ClearImageCacheButton_Click(object sender, RoutedEventArgs e)
    {
        await RunExclusiveUiActionAsync(sender as Control, async () =>
        {
            if (await ConfirmAsync(
                    L("Settings/Dialogs/ClearImageCache/Title", "Clear avatar and image cache?"),
                    L("Settings/Dialogs/ClearImageCache/Body", "This clears cached avatars and images. It does not clear GitHub query payloads or diagnostics."),
                    L("Settings/Dialogs/ClearImageCache/Primary", "Clear images"),
                    "SettingsConfirmClearImageCache"))
            {
                await ViewModel.ClearImageCacheAsync();
            }
        });
    }

    private async void ClearRepoFileCacheButton_Click(object sender, RoutedEventArgs e)
    {
        await RunExclusiveUiActionAsync(sender as Control, async () =>
        {
            if (await ConfirmAsync(
                    L("Settings/Dialogs/ClearRepoFileCache/Title", "Clear repository file cache?"),
                    L("Settings/Dialogs/ClearRepoFileCache/Body", "This clears locally cached repository file previews. Repository trees and other GitHub query data remain available."),
                    L("Settings/Dialogs/ClearRepoFileCache/Primary", "Clear file cache"),
                    "SettingsConfirmClearRepoFileCache"))
            {
                await ViewModel.ClearRepoFileCacheAsync();
            }
        });
    }

    private async void ClearAllCacheButton_Click(object sender, RoutedEventArgs e)
    {
        await RunExclusiveUiActionAsync(sender as Control, async () =>
        {
            if (await ConfirmAsync(
                    L("Settings/Dialogs/ClearAllCache/Title", "Clear all Phase 0 cache data?"),
                    L("Settings/Dialogs/ClearAllCache/Body", "This clears GitHub query metadata, payload files, avatars, images, and repository file previews. It does not clear diagnostics or Stars categories."),
                    L("Settings/Dialogs/ClearAllCache/Primary", "Clear cache data"),
                    "SettingsConfirmClearAllCache"))
            {
                await ViewModel.ClearAllCacheAsync();
            }
        });
    }

    private async void ClearStarLibraryButton_Click(object sender, RoutedEventArgs e)
    {
        await RunExclusiveUiActionAsync(sender as Control, async () =>
        {
            if (await ConfirmAsync(
                    L("Settings/Dialogs/ClearStarsLibrary/Title", "Clear the Stars library?"),
                    L("Settings/Dialogs/ClearStarsLibrary/Body", "This permanently removes the local searchable Stars index and every JitHub category. GitHub stars themselves are not changed."),
                    L("Settings/Dialogs/ClearStarsLibrary/Primary", "Clear Stars library"),
                    "SettingsConfirmClearStarsLibrary"))
            {
                await ViewModel.ClearStarLibraryAsync();
            }
        });
    }

    private async void ExportDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        await RunExclusiveUiActionAsync(sender as Control, async () =>
        {
            FileSavePicker picker = new()
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = "jithub-diagnostics"
            };
            picker.FileTypeChoices.Add("NDJSON diagnostics", [".ndjson"]);

            IntPtr hwnd = WindowNative.GetWindowHandle(((App)Application.Current).CurrentMainWindow);
            InitializeWithWindow.Initialize(picker, hwnd);

            StorageFile? file = await picker.PickSaveFileAsync();
            if (file is not null)
            {
                await ViewModel.ExportDiagnosticsAsync(file.Path);
            }
        });
    }

    private async void SignOutButton_Click(object sender, RoutedEventArgs e)
    {
        await RunExclusiveUiActionAsync(sender as Control, async () =>
        {
            if (XamlRoot is not null)
            {
                await AccountSignOutDialogFlow.ShowAsync(XamlRoot);
            }
        });
    }

    private async void ViewSourceButton_Click(object sender, RoutedEventArgs e)
    {
        await RunExclusiveUiActionAsync(
            sender as Control,
            () => ViewModel.OpenSourceRepositoryAsync());
    }

    private async System.Threading.Tasks.Task<bool> ConfirmAsync(
        string title,
        string body,
        string primaryButtonText,
        string automationId)
    {
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = body,
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = L("Common/Cancel", "Cancel"),
            DefaultButton = ContentDialogButton.Close,
            PrimaryButtonStyle = (Style)Application.Current.Resources["AppDestructiveButtonStyle"]
        };
        AppDialogStyleCatalog.Apply(dialog);
        AutomationProperties.SetAutomationId(dialog, automationId);
        AutomationProperties.SetName(dialog, title);

        ContentDialogResult result = await AppContentDialogPresenter.ShowAsync(dialog, XamlRoot);
        return result == ContentDialogResult.Primary;
    }

    private static string L(string key, string fallback) =>
        LocalizedResourceText.GetString(key, fallback);

    private async Task RunExclusiveUiActionAsync(Control? origin, Func<Task> action)
    {
        try
        {
            await _actionGate.TryRunAsync(action);
        }
        catch (Exception ex)
        {
            ViewModel.ReportActionFailure(ex);
        }
        finally
        {
            await Task.Yield();
            if (!_actionGate.IsActive && origin is { IsEnabled: true, Visibility: Visibility.Visible })
            {
                _ = origin.Focus(FocusState.Programmatic);
            }
        }
    }

    private void SettingsActionGate_StateChanged(object? sender, EventArgs e)
    {
        bool isEnabled = !_actionGate.IsActive;
        SettingsSignOutButton.IsEnabled = isEnabled;
        SettingsClearQueryCacheButton.IsEnabled = isEnabled;
        SettingsClearStarLibraryButton.IsEnabled = isEnabled;
        SettingsClearImageCacheButton.IsEnabled = isEnabled;
        SettingsClearRepoFileCacheButton.IsEnabled = isEnabled;
        SettingsClearAllCacheButton.IsEnabled = isEnabled;
        SettingsExportDiagnosticsButton.IsEnabled = isEnabled;
        SettingsClearDiagnosticsButton.IsEnabled = isEnabled;
    }
}

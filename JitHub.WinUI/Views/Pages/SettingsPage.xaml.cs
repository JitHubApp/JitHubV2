using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using JitHub.Models;
using JitHub.Services;
using JitHub.Services.Layout;
using JitHub.WinUI.Helpers;
using JitHub.WinUI.ViewModels.Pages;
using JitHub.WinUI.Views.Controls.App;
using JitHub.WinUI.Views.Dialogs;
using JitHubModels = JitHub.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;

namespace JitHub.WinUI.Views.Pages;

public sealed partial class SettingsPage : Page
{
    private readonly SettingsActionGate _actionGate = new();
    private bool _isSynchronizingSectionSelection;
    private bool _isSynchronizingThemeCards;
    private int _sectionScrollRequestVersion;

    public SettingsPage()
    {
        ViewModel = ((App)Application.Current).GetService<SettingsPageViewModel>();
        InitializeComponent();
        PopulateSettingsSections();
        PopulateThemePalettes();
        PopulateContributors(SettingsDevelopersList, Developers);
        PopulateContributors(SettingsDesignersList, Designers);
        RegisterThemeCardKeyboardNavigation();
        DataContext = ViewModel;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        _actionGate.StateChanged += SettingsActionGate_StateChanged;
        Loaded += SettingsPage_Loaded;
        Unloaded += SettingsPage_Unloaded;
    }

    public SettingsPageViewModel ViewModel { get; }

    private void PopulateSettingsSections()
    {
        foreach (SettingsSectionItem section in ViewModel.SettingsSections)
        {
            SettingsSectionList.Items.Add(section);
            CompactSectionPicker.Items.Add(section);
        }

        SynchronizeSectionSelection(ViewModel.SelectedSection);
    }

    private void PopulateThemePalettes()
    {
        ThemePaletteRepeater.ItemsSource = ViewModel.PaletteOptions;
    }

    private static void PopulateItems<T>(ItemsControl control, IEnumerable<T> items)
    {
        control.Items.Clear();
        foreach (T item in items)
        {
            control.Items.Add(item);
        }
    }

    private static void PopulateContributors(
        StackPanel panel,
        IEnumerable<CreditPersonale> contributors)
    {
        panel.Children.Clear();
        foreach (CreditPersonale contributor in contributors)
        {
            panel.Children.Add(new AppContributorCard(contributor));
        }
    }

    private void RegisterThemeCardKeyboardNavigation()
    {
        Microsoft.UI.Xaml.Input.KeyEventHandler handler = ThemeButton_KeyDown;
        SystemThemeButton.AddHandler(UIElement.KeyDownEvent, handler, handledEventsToo: true);
        LightThemeButton.AddHandler(UIElement.KeyDownEvent, handler, handledEventsToo: true);
        DarkThemeButton.AddHandler(UIElement.KeyDownEvent, handler, handledEventsToo: true);
    }

    public IReadOnlyList<CreditPersonale> Developers { get; } =
    (CreditPersonale[])
    [
        new(
            "ms-appx:///Assets/ContributorsProfilePhotos/NeroProfile.jpg",
            "Nero Cui",
            "Developer",
            "I'm a software engineer working at Microsoft. I like developing apps, playing video games and sharing my knowledge. JitHub is a personal project of mine, but I have plans to add more and more feature to it.",
            Color.FromArgb(255, 148, 136, 138),
            (PersonalLink[])
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
            (PersonalLink[])
            [
                new PersonalLink("https://github.com/Get0457", PersonalLink.GitHubLogo)
            ]),
        new(
            "",
            "Ze Chen",
            "Developer",
            "Software engineer, always trying to learn something new :)",
            Color.FromArgb(255, 148, 136, 138),
            (PersonalLink[])
            [
                new PersonalLink("https://github.com/billzyc", PersonalLink.GitHubLogo)
            ]),
        new(
            "ms-appx:///Assets/ContributorsProfilePhotos/XueyangProfile.png",
            "Xueyang Song",
            "ML + Battery Researcher",
            "Xueyang is an ML and battery researcher whose publications are widely referenced across academic institutions and industry, connecting data-driven methods with practical energy research.",
            Color.FromArgb(255, 176, 161, 132),
            (PersonalLink[])
            [
                new PersonalLink("https://www.linkedin.com/in/xueyang-song-b79bb9192/", PersonalLink.LinkedInLogo),
                new PersonalLink("https://scholar.google.com/citations?user=4FvfgxkAAAAJ&hl=en", PersonalLink.GoogleScholarLogo)
            ])
    ];

    public IReadOnlyList<CreditPersonale> Designers { get; } =
    (CreditPersonale[])
    [
        new(
            "ms-appx:///Assets/ContributorsProfilePhotos/KeiraProfile.png",
            "Keira Xu",
            "Logo Designer",
            "Keira is a Product Designer at Microsoft, ex-EA, with a passion for interaction UI/UX design, prototyping and video creation. She received the 2017 Red Dot Award for her innovative designs.",
            Color.FromArgb(255, 148, 112, 100),
            (PersonalLink[])
            [
                new PersonalLink("https://www.linkedin.com/in/kejiaxu/", PersonalLink.LinkedInLogo)
            ]),
        new(
            "ms-appx:///Assets/ContributorsProfilePhotos/JakubProfile.png",
            "Jakub Bugajski",
            "UI Designer",
            "Jakub is a 13 year old UI/UX designer from Poland. He got featured on The Verge and many other sites for his File Explorer design.",
            Color.FromArgb(255, 247, 205, 185),
            (PersonalLink[])
            [
                new PersonalLink("https://twitter.com/AlurDesign", PersonalLink.TwitterLogo)
            ])
    ];

    private void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        UiTaskGuard.Run(async () =>
        {
            Loaded -= SettingsPage_Loaded;
            UpdateResponsiveState(ActualWidth);
            try
            {
                await ViewModel.InitializeAsync();
                PopulateItems(SettingsCacheOwnersList, ViewModel.CacheOwners);
                SynchronizeThemeCards();
            }
            catch (Exception ex)
            {
                ViewModel.ReportActionFailure(ex);
            }

            // Settings sections are local, committed content even when a diagnostics refresh fails.
            ProductPerformanceReadiness.CommitRoute("settings", ProductPerformanceReadiness.CountIdentity(ViewModel.SettingsSections.Count));
        }, "ui-settings-page");
    }

    private void SettingsPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _sectionScrollRequestVersion++;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        Unloaded -= SettingsPage_Unloaded;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(SettingsPageViewModel.CacheOwners), StringComparison.Ordinal))
        {
            return;
        }

        if (DispatcherQueue.HasThreadAccess)
        {
            PopulateItems(SettingsCacheOwnersList, ViewModel.CacheOwners);
        }
        else
        {
            _ = DispatcherQueue.TryEnqueue(
                () => PopulateItems(SettingsCacheOwnersList, ViewModel.CacheOwners));
        }
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
        if (_isSynchronizingSectionSelection ||
            sender is not Selector selector ||
            selector.SelectedItem is not SettingsSectionItem section)
        {
            return;
        }

        ViewModel.SelectedSection = section;
        SynchronizeSectionSelection(section);
        int requestVersion = ++_sectionScrollRequestVersion;
        _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (requestVersion != _sectionScrollRequestVersion)
            {
                return;
            }

            SettingsContentScrollViewer.ChangeView(null, 0, null, disableAnimation: true);
        });
    }

    private void SynchronizeSectionSelection(SettingsSectionItem section)
    {
        _isSynchronizingSectionSelection = true;
        try
        {
            SettingsSectionList.SelectedItem = section;
            CompactSectionPicker.SelectedItem = section;
        }
        finally
        {
            _isSynchronizingSectionSelection = false;
        }
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

    private void ThemePaletteRepeater_ElementPrepared(
        ItemsRepeater sender,
        ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is RadioButton card)
        {
            Microsoft.UI.Xaml.Input.KeyEventHandler keyHandler = PaletteButton_KeyDown;
            card.Checked -= PaletteButton_Checked;
            card.Checked += PaletteButton_Checked;
            card.RemoveHandler(UIElement.KeyDownEvent, keyHandler);
            card.AddHandler(UIElement.KeyDownEvent, keyHandler, handledEventsToo: true);
        }
    }

    private void ThemePaletteRepeater_ElementClearing(
        ItemsRepeater sender,
        ItemsRepeaterElementClearingEventArgs args)
    {
        if (args.Element is RadioButton card)
        {
            Microsoft.UI.Xaml.Input.KeyEventHandler keyHandler = PaletteButton_KeyDown;
            card.Checked -= PaletteButton_Checked;
            card.RemoveHandler(UIElement.KeyDownEvent, keyHandler);
        }
    }

    private void PaletteButton_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { IsChecked: true, Tag: string paletteId })
        {
            ViewModel.SelectPalette(paletteId);
        }
    }

    private void PaletteButton_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string paletteId })
        {
            return;
        }

        int direction = e.Key switch
        {
            VirtualKey.Left or VirtualKey.Up => -1,
            VirtualKey.Right or VirtualKey.Down => 1,
            _ => 0
        };
        if (direction == 0)
        {
            return;
        }

        int currentIndex = -1;
        for (int index = 0; index < ViewModel.PaletteOptions.Count; index++)
        {
            if (string.Equals(ViewModel.PaletteOptions[index].Id, paletteId, StringComparison.Ordinal))
            {
                currentIndex = index;
                break;
            }
        }

        if (currentIndex < 0)
        {
            return;
        }

        int nextIndex = (currentIndex + direction + ViewModel.PaletteOptions.Count) % ViewModel.PaletteOptions.Count;
        ViewModel.SelectedPaletteOption = ViewModel.PaletteOptions[nextIndex];
        if (ThemePaletteRepeater.GetOrCreateElement(nextIndex) is RadioButton nextCard)
        {
            _ = nextCard.Focus(FocusState.Keyboard);
        }

        e.Handled = true;
    }

    private void ResetPaletteButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ResetPalette();
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

    private void RetryDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        UiTaskGuard.Run(async () =>
        {
            try
            {
                await ViewModel.RefreshDiagnosticsAsync();
            }
            catch (Exception ex)
            {
                ViewModel.ReportActionFailure(ex);
            }
        }, "ui-settings-page");
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

    private void ClearDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        UiTaskGuard.Run(async () =>
        {
            await RunExclusiveUiActionAsync(sender as Control, async () =>
            {
                if (await ConfirmAsync(L("Settings/Dialogs/ClearDiagnostics/Title", "Clear diagnostics?"), L("Settings/Dialogs/ClearDiagnostics/Body", "This clears the local diagnostics NDJSON file. It does not change telemetry settings or cached GitHub data."), L("Settings/Dialogs/ClearDiagnostics/Primary", "Clear diagnostics"), "SettingsConfirmClearDiagnostics"))
                {
                    await ViewModel.ClearDiagnosticsAsync();
                }
            });
        }, "ui-settings-page");
    }

    private void ClearQueryCacheButton_Click(object sender, RoutedEventArgs e)
    {
        UiTaskGuard.Run(async () =>
        {
            await RunExclusiveUiActionAsync(sender as Control, async () =>
            {
                if (await ConfirmAsync(L("Settings/Dialogs/ClearQueryCache/Title", "Clear GitHub query cache?"), L("Settings/Dialogs/ClearQueryCache/Body", "This clears cached GitHub query metadata and JSON/blob/diff payload files. It does not clear avatar images or diagnostics."), L("Settings/Dialogs/ClearQueryCache/Primary", "Clear query cache"), "SettingsConfirmClearQueryCache"))
                {
                    await ViewModel.ClearQueryCacheAsync();
                }
            });
        }, "ui-settings-page");
    }

    private void ClearImageCacheButton_Click(object sender, RoutedEventArgs e)
    {
        UiTaskGuard.Run(async () =>
        {
            await RunExclusiveUiActionAsync(sender as Control, async () =>
            {
                if (await ConfirmAsync(L("Settings/Dialogs/ClearImageCache/Title", "Clear avatar and image cache?"), L("Settings/Dialogs/ClearImageCache/Body", "This clears cached avatars and images. It does not clear GitHub query payloads or diagnostics."), L("Settings/Dialogs/ClearImageCache/Primary", "Clear images"), "SettingsConfirmClearImageCache"))
                {
                    await ViewModel.ClearImageCacheAsync();
                }
            });
        }, "ui-settings-page");
    }

    private void ClearRepoFileCacheButton_Click(object sender, RoutedEventArgs e)
    {
        UiTaskGuard.Run(async () =>
        {
            await RunExclusiveUiActionAsync(sender as Control, async () =>
            {
                if (await ConfirmAsync(L("Settings/Dialogs/ClearRepoFileCache/Title", "Clear repository file cache?"), L("Settings/Dialogs/ClearRepoFileCache/Body", "This clears locally cached repository file previews. Repository trees and other GitHub query data remain available."), L("Settings/Dialogs/ClearRepoFileCache/Primary", "Clear file cache"), "SettingsConfirmClearRepoFileCache"))
                {
                    await ViewModel.ClearRepoFileCacheAsync();
                }
            });
        }, "ui-settings-page");
    }

    private void ClearAllCacheButton_Click(object sender, RoutedEventArgs e)
    {
        UiTaskGuard.Run(async () =>
        {
            await RunExclusiveUiActionAsync(sender as Control, async () =>
            {
                if (await ConfirmAsync(L("Settings/Dialogs/ClearAllCache/Title", "Clear all Phase 0 cache data?"), L("Settings/Dialogs/ClearAllCache/Body", "This clears GitHub query metadata, payload files, avatars, images, and repository file previews. It does not clear diagnostics or Stars categories."), L("Settings/Dialogs/ClearAllCache/Primary", "Clear cache data"), "SettingsConfirmClearAllCache"))
                {
                    await ViewModel.ClearAllCacheAsync();
                }
            });
        }, "ui-settings-page");
    }

    private void ClearStarLibraryButton_Click(object sender, RoutedEventArgs e)
    {
        UiTaskGuard.Run(async () =>
        {
            await RunExclusiveUiActionAsync(sender as Control, async () =>
            {
                if (await ConfirmAsync(L("Settings/Dialogs/ClearStarsLibrary/Title", "Clear the Stars library?"), L("Settings/Dialogs/ClearStarsLibrary/Body", "This permanently removes the local searchable Stars index and every JitHub category. GitHub stars themselves are not changed."), L("Settings/Dialogs/ClearStarsLibrary/Primary", "Clear Stars library"), "SettingsConfirmClearStarsLibrary"))
                {
                    await ViewModel.ClearStarLibraryAsync();
                }
            });
        }, "ui-settings-page");
    }

    private void ExportDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        UiTaskGuard.Run(async () =>
        {
            await RunExclusiveUiActionAsync(sender as Control, async () =>
            {
                FileSavePicker picker = new(((App)Application.Current).CurrentMainWindow.AppWindow.Id)
                {
                    SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                    SuggestedFileName = "jithub-diagnostics"
                };
                picker.FileTypeChoices.Add("NDJSON diagnostics", [".ndjson"]);
                PickFileResult? file = await picker.PickSaveFileAsync();
                if (file is not null)
                {
                    await ViewModel.ExportDiagnosticsAsync(file.Path);
                }
            });
        }, "ui-settings-page");
    }

    private void SignOutButton_Click(object sender, RoutedEventArgs e)
    {
        UiTaskGuard.Run(async () =>
        {
            await RunExclusiveUiActionAsync(sender as Control, async () =>
            {
                if (XamlRoot is not null)
                {
                    await AccountSignOutDialogFlow.ShowAsync(XamlRoot);
                }
            });
        }, "ui-settings-page");
    }

    private void ViewSourceButton_Click(object sender, RoutedEventArgs e)
    {
        UiTaskGuard.Run(async () =>
        {
            await RunExclusiveUiActionAsync(sender as Control, () => ViewModel.OpenSourceRepositoryAsync());
        }, "ui-settings-page");
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

        ContentDialogResult result = await AppContentDialogPresenter.ShowAsync(
            dialog,
            XamlRoot,
            AppDialogLayoutKind.Confirmation);
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

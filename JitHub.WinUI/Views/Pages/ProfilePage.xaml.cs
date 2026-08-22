using System;
using System.ComponentModel;
using System.Globalization;
using System.Threading.Tasks;
using JitHub.Models.NavArgs;
using JitHub.Services;
using JitHub.Services.Layout;
using JitHub.WinUI.ViewModels.Pages;
using JitHub.WinUI.Views.Controls.Common;
using JitHub.WinUI.Views.Dialogs;
using JitHub.WinUI.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;

namespace JitHub.WinUI.Views.Pages;

public sealed partial class ProfilePage : Page
{
    private bool _initialized;
    private bool _syncingModeSelection;
    private string _loadedProfileKey = string.Empty;
    private readonly IGitHubStarLibraryService _starLibraryService;

    public ProfilePageViewModel ViewModel { get; }

    public string CompactEditActionText =>
        LocalizedResourceText.GetString(
            "PagesProfilePageProfileEditButton/Content",
            "Edit profile");

    public ProfilePage()
    {
        NavigationCacheMode = NavigationCacheMode.Required;
        ViewModel = ((App)Application.Current).GetService<ProfilePageViewModel>();
        _starLibraryService = ((App)Application.Current).GetService<IGitHubStarLibraryService>();
        InitializeComponent();
        DataContext = ViewModel;

        ProfileOverviewReadmeViewer.HostKind = MarkdownHostContract.ProfileReadme;
        ProfileOverviewReadmeViewer.AutomationInstanceId = "ProfileOverviewReadme";

        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        Loaded += ProfilePage_Loaded;
        Unloaded += ProfilePage_Unloaded;
        SyncModeSelection();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ApplyResponsiveLayout(ActualWidth);
        UserProfilePageArgs? args = e.Parameter as UserProfilePageArgs;
        string profileKey = CreateProfileKey(args);
        if (_initialized && string.Equals(_loadedProfileKey, profileKey, StringComparison.Ordinal))
        {
            CommitPerformanceReadiness();
            return;
        }

        _initialized = true;
        _loadedProfileKey = profileKey;
        await ViewModel.InitializeAsync(args);
        CommitPerformanceReadiness();
        SyncModeSelection();
    }

    private void CommitPerformanceReadiness() =>
        ProductPerformanceReadiness.CommitRoute(
            "profile",
            ViewModel.CurrentUser is { Id: > 0 } user
                ? $"user={user.Id.ToString(CultureInfo.InvariantCulture)}"
                : "user=empty");

    private static string CreateProfileKey(UserProfilePageArgs? args)
    {
        string login = args?.Login?.Trim().ToLowerInvariant() ?? string.Empty;
        string userId = args?.UserId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        return string.IsNullOrEmpty(login) && string.IsNullOrEmpty(userId)
            ? "authenticated"
            : $"{userId}:{login}";
    }

    private void ProfilePage_Loaded(object sender, RoutedEventArgs e)
    {
        _starLibraryService.Changed -= StarLibraryService_Changed;
        _starLibraryService.Changed += StarLibraryService_Changed;
    }

    private void ProfilePage_Unloaded(object sender, RoutedEventArgs e)
    {
        _starLibraryService.Changed -= StarLibraryService_Changed;
    }

    private void StarLibraryService_Changed(object? sender, StarLibraryChangedEventArgs e)
    {
        if (e.Kind == StarLibraryChangeKind.ProjectionInvalidated)
        {
            DispatcherQueue.TryEnqueue(() => ViewModel.NotifyStarLibraryChanged(e.UserId));
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ProfilePageViewModel.ActiveMode)
            or nameof(ProfilePageViewModel.IsEditVisible))
        {
            SyncModeSelection();
        }
    }

    private void Page_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyResponsiveLayout(e.NewSize.Width);

    private void ApplyResponsiveLayout(double width)
    {
        WorkspaceChromeState chrome = WorkspaceChromeLayout.Calculate(
            width,
            WorkspaceChromeContracts.Profile);
        WorkspaceChromeVisuals.ApplyRoot(ProfileRoot, chrome);
        WorkspaceChromeVisuals.ApplyHeader(ProfileHeaderGrid, chrome);

        ProfileBoard.Width = Math.Min(ProfileBoard.MaxWidth, chrome.ContentBounds.Arrange(ProfileBoard.DesiredSize.Width));
        bool compact = chrome.Mode != WorkspaceChromeMode.Wide;
        IdentityColumn.Width = compact ? new GridLength(0) : new GridLength(304);
        IdentityRail.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        CompactIdentityPanel.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        CompactIdentityRow.Height = compact ? GridLength.Auto : new GridLength(0);
        Grid.SetColumn(MainColumnHost, compact ? 0 : 1);
        Grid.SetColumnSpan(MainColumnHost, compact ? 2 : 1);

        WorkspaceChromeVisuals.ApplyPlacement(
            ProfileModeSelectorHost,
            chrome,
            new WorkspaceElementPlacement(0, 0, 1, StretchHorizontally: true),
            new WorkspaceElementPlacement(0, 0, 2, StretchHorizontally: true));
        WorkspaceChromeVisuals.ApplyPlacement(
            ProfileHeaderStatusHost,
            chrome,
            new WorkspaceElementPlacement(0, 1, 1),
            new WorkspaceElementPlacement(1, 0, 2));
        WorkspaceChromeVisuals.ApplyOptionalContext(ProfileOptionalHeaderContextHost, chrome);

        WorkspaceChromeVisuals.ApplyPlacement(
            CompactIdentityActions,
            chrome,
            new WorkspaceElementPlacement(0, 2, 1),
            new WorkspaceElementPlacement(1, 0, 3));
        WorkspaceChromeVisuals.ApplyActionLabel(ProfileCompactEditButtonText, chrome);
        WorkspaceChromeVisuals.ApplyActionLabel(ProfileCompactFollowButtonText, chrome);
        WorkspaceChromeVisuals.ApplyActionButton(
            ProfileCompactEditButton,
            chrome,
            hasVisibleLabel: chrome.ShowActionLabels);
        WorkspaceChromeVisuals.ApplyActionButton(
            ProfileCompactFollowButton,
            chrome,
            hasVisibleLabel: chrome.ShowActionLabels);
    }

    private void ProfileModeSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_syncingModeSelection || sender.SelectedItem is null)
        {
            return;
        }

        ProfileWorkspaceMode mode = ReferenceEquals(sender.SelectedItem, RepositoriesModeItem)
            ? ProfileWorkspaceMode.Repositories
            : ReferenceEquals(sender.SelectedItem, StarsModeItem)
                ? ProfileWorkspaceMode.Stars
                : ReferenceEquals(sender.SelectedItem, ActivityModeItem)
                    ? ProfileWorkspaceMode.Activity
                    : ReferenceEquals(sender.SelectedItem, ReadmeModeItem)
                        ? ProfileWorkspaceMode.Readme
                        : ProfileWorkspaceMode.Overview;

        ViewModel.SetActiveMode(mode);
        SyncModeSelection();
    }

    private void ProfileModeSelector_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        int direction = e.Key switch
        {
            VirtualKey.Left => -1,
            VirtualKey.Right => 1,
            _ => 0
        };
        if (direction == 0)
        {
            return;
        }

        SelectorBarItem[] items =
        [
            OverviewModeItem,
            RepositoriesModeItem,
            StarsModeItem,
            ActivityModeItem,
            ReadmeModeItem
        ];
        int currentIndex = Array.IndexOf(items, ProfileModeSelector.SelectedItem);
        if (currentIndex < 0)
        {
            currentIndex = 0;
        }

        for (int index = currentIndex + direction; index >= 0 && index < items.Length; index += direction)
        {
            if (items[index].Visibility != Visibility.Visible)
            {
                continue;
            }

            ProfileModeSelector.SelectedItem = items[index];
            items[index].Focus(FocusState.Keyboard);
            e.Handled = true;
            return;
        }
    }

    private void SyncModeSelection()
    {
        SelectorBarItem item = ViewModel.ActiveMode switch
        {
            ProfileWorkspaceMode.Repositories => RepositoriesModeItem,
            ProfileWorkspaceMode.Stars => StarsModeItem,
            ProfileWorkspaceMode.Activity => ActivityModeItem,
            ProfileWorkspaceMode.Readme => ReadmeModeItem,
            _ => OverviewModeItem
        };

        if (ReferenceEquals(ProfileModeSelector.SelectedItem, item))
        {
            return;
        }

        _syncingModeSelection = true;
        ProfileModeSelector.SelectedItem = item;
        _syncingModeSelection = false;
    }

    private void ProfileList_ContainerContentChanging(
        ListViewBase sender,
        ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is not ListViewItem container || args.Item is null)
        {
            return;
        }

        switch (args.Item)
        {
            case ProfileRepositoryViewItem repository:
                AutomationProperties.SetAutomationId(container, repository.AutomationId);
                AutomationProperties.SetName(container, repository.AccessibleName);
                break;
            case ProfilePersonItem person:
                AutomationProperties.SetAutomationId(container, person.AutomationId);
                AutomationProperties.SetName(container, person.AccessibleName);
                break;
            case ProfileActivityItem activity:
                AutomationProperties.SetAutomationId(container, activity.AutomationId);
                AutomationProperties.SetName(container, activity.AccessibleName);
                break;
        }

        if (!args.InRecycleQueue && args.ItemIndex >= Math.Max(0, sender.Items.Count - 6))
        {
            _ = LoadNextPageForListAsync(sender);
        }
    }

    private async Task LoadNextPageForListAsync(ListViewBase list)
    {
        ProfileWorkspaceMode? mode = ReferenceEquals(list, RepositoriesList)
            ? ProfileWorkspaceMode.Repositories
            : ReferenceEquals(list, StarsList)
                ? ProfileWorkspaceMode.Stars
                : ReferenceEquals(list, ActivityList)
                    ? ProfileWorkspaceMode.Activity
                    : ReferenceEquals(list, FollowersList)
                        ? ProfileWorkspaceMode.Followers
                        : ReferenceEquals(list, FollowingList)
                            ? ProfileWorkspaceMode.Following
                            : null;
        if (mode is null)
        {
            return;
        }

        try
        {
            await ViewModel.LoadNextPageAsync(mode.Value);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ViewModel.ShowSectionLoadError(ex);
        }
    }

    private void RepositoryList_ItemClick(object sender, ItemClickEventArgs e) =>
        ViewModel.OpenRepository(e.ClickedItem as ProfileRepositoryViewItem);

    private void PeopleList_ItemClick(object sender, ItemClickEventArgs e) =>
        ViewModel.OpenPerson(e.ClickedItem as ProfilePersonItem, "profile_people_list");

    private void ActivityList_ItemClick(object sender, ItemClickEventArgs e) =>
        ViewModel.OpenActivity(e.ClickedItem as ProfileActivityItem);

    private void ProfileReadmeViewer_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is MarkdownViewer viewer)
        {
            viewer.HostKind = MarkdownHostContract.ProfileReadme;
            viewer.AutomationInstanceId = "ProfileReadme";
            viewer.SetBinding(MarkdownViewer.DocumentSourceProperty, new Binding
            {
                Source = ViewModel,
                Path = new PropertyPath(nameof(ProfilePageViewModel.ReadmeDocumentSource)),
                Mode = BindingMode.OneWay
            });
        }
    }

    private void RepositoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ProfileRepositoryViewItem repository })
        {
            ViewModel.OpenRepository(repository);
        }
    }

    private void OrganizationButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ProfileOrganizationViewItem organization })
        {
            ViewModel.OpenOrganization(organization);
        }
    }

    private async void ProfileFactButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ProfileFactItem fact })
        {
            await ViewModel.OpenFactAsync(fact);
        }
    }

    private async void ProfileFactOpenMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ProfileFactItem fact })
        {
            await ViewModel.OpenFactAsync(fact);
        }
    }

    private void ProfileFactCopyMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ProfileFactItem { IsActionable: true } fact } &&
            !string.IsNullOrWhiteSpace(fact.CopyValue))
        {
            ViewModel.TrackFactCopy(PlatformHelper.CopyString(fact.CopyValue));
        }
    }

    private void RepositoriesStatButton_Click(object sender, RoutedEventArgs e) =>
        ViewModel.OpenRepositoriesStat();

    private void FollowersStatButton_Click(object sender, RoutedEventArgs e) =>
        ViewModel.OpenFollowersStat();

    private void FollowingStatButton_Click(object sender, RoutedEventArgs e) =>
        ViewModel.OpenFollowingStat();

    private async void GistsStatButton_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.OpenGistsStatAsync();

    private void StarsLibraryButton_Click(object sender, RoutedEventArgs e) =>
        ViewModel.OpenStarsLibrary();

    private void PeopleBackButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ReturnToOverview();
        ProfileModeSelector.Focus(FocusState.Programmatic);
    }

    private async void OpenOnGitHubButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.OpenProfileExternallyAsync();
    }

    private async void EditProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsEditVisible)
        {
            return;
        }

        ProfileEditDraft draft = ViewModel.CreateEditDraft();
        TextBlock errorText = AppContentDialogPresenter.CreateInlineErrorPresenter("ProfileEditDialogError");
        ScrollViewer fieldsScroller = new()
        {
            Content = CreateEditProfileContent(draft),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Enabled,
            MaxHeight = Math.Max(280, XamlRoot.Size.Height - 240)
        };
        AutomationProperties.SetAutomationId(fieldsScroller, "ProfileEditFieldsScrollViewer");
        AutomationProperties.SetName(fieldsScroller, T("Profile/Edit/Fields", "Profile fields"));
        errorText.Margin = new Thickness(0, 12, 0, 0);
        AppDialogScrollableContent dialogContent = new()
        {
            RowDefinitions =
            {
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
                new RowDefinition { Height = GridLength.Auto }
            }
        };
        dialogContent.Children.Add(fieldsScroller);
        dialogContent.Children.Add(errorText);
        Grid.SetRow(errorText, 1);
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = T("Profile/Edit/Title", "Edit profile"),
            PrimaryButtonText = T("Common/Save", "Save"),
            CloseButtonText = T("Common/Cancel", "Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            Content = dialogContent
        };
        AppDialogStyleCatalog.Apply(dialog);
        AutomationProperties.SetAutomationId(dialog, "ProfileEditDialog");
        AutomationProperties.SetName(dialog, T("Profile/Edit/Title", "Edit profile"));

        await AppContentDialogPresenter.ShowForPrimaryActionAsync(
            dialog,
            XamlRoot,
            async () => await ViewModel.SaveProfileAsync(draft)
                ? DialogMutationResult.Success()
                : DialogMutationResult.Failure(ViewModel.StatusText),
            errorText);
    }

    private static Grid CreateEditProfileContent(ProfileEditDraft draft)
    {
        Grid grid = new()
        {
            RowSpacing = 12,
            MinWidth = 0,
            MaxWidth = 520,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        for (int row = 0; row < 7; row++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        AddEditRow(grid, CreateEditTextBox(T("Profile/Edit/Name", "Name"), "ProfileEditNameBox", draft.Name, value => draft.Name = value, 80), 0);
        AddEditRow(grid, CreateEditTextBox(T("Profile/Edit/Bio", "Bio"), "ProfileEditBioBox", draft.Bio, value => draft.Bio = value, 160, true), 1);
        AddEditRow(grid, CreateEditTextBox(T("Profile/Edit/Company", "Company"), "ProfileEditCompanyBox", draft.Company, value => draft.Company = value, 80), 2);
        AddEditRow(grid, CreateEditTextBox(T("Profile/Edit/Location", "Location"), "ProfileEditLocationBox", draft.Location, value => draft.Location = value, 80), 3);
        AddEditRow(grid, CreateEditTextBox(T("Profile/Edit/Website", "Website"), "ProfileEditWebsiteBox", draft.Blog, value => draft.Blog = value, 120), 4);
        AddEditRow(grid, CreateEditTextBox(T("Profile/Edit/Twitter", "Twitter username"), "ProfileEditTwitterBox", draft.TwitterUsername, value => draft.TwitterUsername = value, 80), 5);

        string hireableText = T("Profile/Edit/AvailableForHire", "Available for hire");
        ToggleSwitch hireable = new() { Header = hireableText, IsOn = draft.Hireable };
        AutomationProperties.SetAutomationId(hireable, "ProfileEditHireableToggle");
        AutomationProperties.SetName(hireable, hireableText);
        hireable.Toggled += (_, _) => draft.Hireable = hireable.IsOn;
        AddEditRow(grid, hireable, 6);
        return grid;
    }

    private static TextBox CreateEditTextBox(
        string header,
        string automationId,
        string text,
        Action<string> update,
        int maxLength,
        bool acceptsReturn = false)
    {
        TextBox textBox = new()
        {
            Header = header,
            Text = text,
            MaxLength = maxLength,
            AcceptsReturn = acceptsReturn,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = acceptsReturn ? 88 : 0
        };
        AutomationProperties.SetAutomationId(textBox, automationId);
        AutomationProperties.SetName(textBox, header);
        textBox.TextChanged += (_, _) => update(textBox.Text);
        return textBox;
    }

    private static void AddEditRow(Grid grid, FrameworkElement element, int row)
    {
        Grid.SetRow(element, row);
        grid.Children.Add(element);
    }

    private static string T(string key, string fallback) =>
        LocalizedResourceText.GetString(key, fallback);
}

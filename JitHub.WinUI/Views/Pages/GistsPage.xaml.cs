using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Services;
using JitHub.Services.Layout;
using JitHub.WinUI.Helpers;
using JitHub.WinUI.Performance;
using JitHub.WinUI.ViewModels.Pages;
using JitHub.WinUI.Views.Controls.Common;
using JitHub.WinUI.Views.Dialogs;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Core;

namespace JitHub.WinUI.Views.Pages;

public sealed partial class GistsPage : Page
{
    private bool _initialized;
    private bool _eventsSubscribed;
    private Task _stopTask = Task.CompletedTask;
    private CancellationTokenSource? _pageCancellationTokenSource;
    private ContentDialog? _activeDialog;
    private DataTransferManager? _shareManager;
    private GistViewItem? _shareItem;
    private ListViewScrollAnchor? _pendingLibraryScrollAnchor;
    private long _gistTraversalGeneration;
    private string? _lastGistTraversalKey;
    private ProductPerformanceScrollProbe? _performanceScrollProbe;

    public GistsPageViewModel ViewModel { get; }

    public GistsPage()
    {
        ViewModel = ((App)Application.Current).GetService<GistsPageViewModel>();
        InitializeComponent();
        DataContext = ViewModel;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void Page_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyResponsiveLayout(e.NewSize.Width);

    private void ApplyResponsiveLayout(double availableWidth)
    {
        WorkspaceChromeState chrome = WorkspaceChromeLayout.Calculate(
            availableWidth,
            WorkspaceChromeContracts.Gists);
        WorkspaceChromeVisuals.ApplyRoot(GistsLibraryRoot, chrome);
        WorkspaceChromeVisuals.ApplyRoot(GistsDetailRoot, chrome);
        WorkspaceChromeVisuals.ApplyHeader(GistsHeaderGrid, chrome);
        WorkspaceChromeVisuals.ApplyActionLabel(GistsNewButtonText, chrome);
        WorkspaceChromeVisuals.ApplyActionButton(
            GistsNewButton,
            chrome,
            hasVisibleLabel: chrome.ShowActionLabels);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyResponsiveLayout(ActualWidth);
        AttachPerformanceScrollProbe();
        await AwaitLatestStopAsync();
        if (_initialized)
        {
            return;
        }

        _pageCancellationTokenSource?.Dispose();
        _pageCancellationTokenSource = new CancellationTokenSource();
        SubscribeViewModelEvents();
        _initialized = true;
        try
        {
            await ViewModel.InitializeAsync(_pageCancellationTokenSource.Token);
            ProductPerformanceReadiness.CommitRoute(
                "gists",
                ProductPerformanceReadiness.CountIdentity(ViewModel.Gists.Count));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to initialize gists: {ex}");
        }
    }

    private async void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Interlocked.Increment(ref _gistTraversalGeneration);
        _performanceScrollProbe?.Dispose();
        _performanceScrollProbe = null;
        UnsubscribeViewModelEvents();
        CancellationTokenSource? pageCancellationTokenSource = Interlocked.Exchange(
            ref _pageCancellationTokenSource,
            null);
        pageCancellationTokenSource?.Cancel();
        _activeDialog = null;
        if (_shareManager is not null)
        {
            _shareManager.DataRequested -= ShareManager_DataRequested;
            _shareManager = null;
        }

        _shareItem = null;
        _initialized = false;
        Task priorStop = _stopTask;
        _stopTask = StopAfterAsync(priorStop, pageCancellationTokenSource);
        try
        {
            await _stopTask;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to stop Gists background work: {ex}");
        }
    }

    private void AttachPerformanceScrollProbe()
    {
        _performanceScrollProbe?.Dispose();
        _performanceScrollProbe = ProductPerformanceReadiness.IsEnabled &&
            FindDescendant<ScrollViewer>(GistsList) is ScrollViewer scrollViewer
                ? ProductPerformanceScrollProbe.TryStart(GistsList, scrollViewer)
                : null;
    }

    private static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is T nested)
            {
                return nested;
            }
        }

        return null;
    }

    private async Task AwaitLatestStopAsync()
    {
        while (true)
        {
            Task stop = _stopTask;
            await stop;
            if (ReferenceEquals(stop, _stopTask))
            {
                return;
            }
        }
    }

    private async Task StopAfterAsync(Task priorStop, CancellationTokenSource? pageCancellationTokenSource)
    {
        try
        {
            try
            {
                await priorStop;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"A prior Gists stop operation failed: {ex}");
            }

            await ViewModel.StopAsync();
        }
        finally
        {
            pageCancellationTokenSource?.Dispose();
        }
    }

    private void SubscribeViewModelEvents()
    {
        if (_eventsSubscribed)
        {
            return;
        }

        ViewModel.NewGistRequested += ViewModel_NewGistRequested;
        ViewModel.EditGistRequested += ViewModel_EditGistRequested;
        ViewModel.DeleteGistRequested += ViewModel_DeleteGistRequested;
        ViewModel.ShareRequested += ViewModel_ShareRequested;
        ViewModel.CopyRequested += ViewModel_CopyRequested;
        ViewModel.CopyFileRequested += ViewModel_CopyFileRequested;
        ViewModel.SaveFileRequested += ViewModel_SaveFileRequested;
        ViewModel.VisibleProjectionApplying += ViewModel_VisibleProjectionApplying;
        ViewModel.VisibleProjectionApplied += ViewModel_VisibleProjectionApplied;
        _eventsSubscribed = true;
    }

    private void UnsubscribeViewModelEvents()
    {
        if (!_eventsSubscribed)
        {
            return;
        }

        ViewModel.NewGistRequested -= ViewModel_NewGistRequested;
        ViewModel.EditGistRequested -= ViewModel_EditGistRequested;
        ViewModel.DeleteGistRequested -= ViewModel_DeleteGistRequested;
        ViewModel.ShareRequested -= ViewModel_ShareRequested;
        ViewModel.CopyRequested -= ViewModel_CopyRequested;
        ViewModel.CopyFileRequested -= ViewModel_CopyFileRequested;
        ViewModel.SaveFileRequested -= ViewModel_SaveFileRequested;
        ViewModel.VisibleProjectionApplying -= ViewModel_VisibleProjectionApplying;
        ViewModel.VisibleProjectionApplied -= ViewModel_VisibleProjectionApplied;
        _pendingLibraryScrollAnchor?.RestoreAfterCollectionChange(DispatcherQueue);
        _pendingLibraryScrollAnchor = null;
        _eventsSubscribed = false;
    }

    private void ViewModel_VisibleProjectionApplying(object? sender, EventArgs e) =>
        _pendingLibraryScrollAnchor = ListViewScrollAnchor.Capture(
            GistsList,
            static item => (item as GistViewItem)?.StableKey);

    private void ViewModel_VisibleProjectionApplied(object? sender, EventArgs e)
    {
        ListViewScrollAnchor? anchor = _pendingLibraryScrollAnchor;
        _pendingLibraryScrollAnchor = null;
        anchor?.RestoreAfterCollectionChange(DispatcherQueue);
    }

    private async void ViewModel_NewGistRequested(object? sender, EventArgs e)
    {
        if (_activeDialog is not null)
        {
            return;
        }

        GistEditorSession session = ViewModel.CreateNewEditorSession();
        await ShowEditorAsync(session, ViewModel.CreateGistAsync);
    }

    private async void ViewModel_EditGistRequested(object? sender, EventArgs e)
    {
        if (_activeDialog is not null)
        {
            return;
        }

        GistEditorSession? session = ViewModel.CreateEditEditorSession();
        if (session is null)
        {
            return;
        }

        await ShowEditorAsync(session, ViewModel.UpdateSelectedGistAsync);
    }

    private async Task ShowEditorAsync(
        GistEditorSession session,
        Func<GistEditorSession, System.Threading.CancellationToken, Task<bool>> saveAsync)
    {
        FrameworkElement editor = CreateEditor(session);
        editor.HorizontalAlignment = HorizontalAlignment.Stretch;
        editor.Height = Math.Clamp(XamlRoot.Size.Height - 210, 220, 620);
        TextBlock errorText = AppContentDialogPresenter.CreateInlineErrorPresenter("GistEditorDialogError");
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = session.Title,
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    editor,
                    errorText
                }
            },
            PrimaryButtonText = T("Common/Save", "Save"),
            CloseButtonText = T("Common/Cancel", "Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = session.CanSave
        };
        AppDialogStyleCatalog.Apply(dialog);
        AutomationProperties.SetAutomationId(dialog, "GistEditorDialog");
        AutomationProperties.SetName(dialog, session.Title);
        _activeDialog = dialog;
        bool isSubmitting = false;
        System.ComponentModel.PropertyChangedEventHandler validationChanged = (_, args) =>
        {
            if (!isSubmitting && args.PropertyName is nameof(GistEditorSession.CanSave))
            {
                dialog.IsPrimaryButtonEnabled = session.CanSave;
            }
        };
        session.PropertyChanged += validationChanged;
        try
        {
            await AppContentDialogPresenter.ShowForPrimaryActionAsync(
                dialog,
                XamlRoot,
                async () =>
                {
                    isSubmitting = true;
                    try
                    {
                        if (_pageCancellationTokenSource is not { } pageCancellationTokenSource)
                        {
                            return DialogMutationResult.Failure(T("Common/PageUnavailable", "This page is no longer available."));
                        }

                        CancellationToken cancellationToken = pageCancellationTokenSource.Token;
                        bool saved = await saveAsync(session, cancellationToken);
                        return saved
                            ? DialogMutationResult.Success()
                            : DialogMutationResult.Failure(ViewModel.ErrorMessage);
                    }
                    finally
                    {
                        isSubmitting = false;
                    }
                },
                errorText,
                canSubmit: () => session.CanSave,
                layoutKind: AppDialogLayoutKind.Editor);
        }
        finally
        {
            session.PropertyChanged -= validationChanged;
            if (ReferenceEquals(_activeDialog, dialog))
            {
                _activeDialog = null;
            }
        }
    }

    private FrameworkElement CreateEditor(GistEditorSession session)
    {
        Grid root = new()
        {
            MinWidth = 0,
            MinHeight = 220,
            RowSpacing = 12
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        TextBox description = CreateEditorTextBox(
            "GistEditorDescription",
            T("Gists/Editor/DescriptionAutomationName", "Gist description"),
            T("Gists/Editor/Description", "Description"),
            T("Gists/Editor/OptionalDescription", "Optional description"));
        description.MaxLength = 256;
        description.Text = session.Description;
        description.TextChanged += (_, _) => session.Description = description.Text;
        root.Children.Add(description);
        ToggleSwitch visibility = new()
        {
            Header = T("Gists/Editor/Visibility", "Visibility"),
            OffContent = T("Gists/Editor/Secret", "Secret"),
            OnContent = T("Gists/Editor/Public", "Public"),
            IsEnabled = session.CanChangeVisibility,
            IsOn = session.IsPublic,
            MinWidth = 180,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetAutomationId(visibility, "GistEditorVisibility");
        AutomationProperties.SetName(visibility, T("Gists/Editor/VisibilityAutomationName", "Gist visibility"));
        visibility.Toggled += (_, _) => session.IsPublic = visibility.IsOn;
        Grid.SetRow(visibility, 1);
        root.Children.Add(visibility);

        Grid filesArea = new() { ColumnSpacing = 12, RowSpacing = 10 };
        filesArea.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(188) });
        filesArea.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        filesArea.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        filesArea.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0) });
        Grid.SetRow(filesArea, 2);

        Grid fileRail = new() { RowSpacing = 8 };
        fileRail.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        fileRail.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        fileRail.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        fileRail.Children.Add(new TextBlock
        {
            Text = T("Gists/Editor/Files", "Files"),
            FontSize = (double)Application.Current.Resources["AppFontSize13"],
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AppInkBrush"]
        });
        ListView files = new()
        {
            ItemsSource = session.Files,
            DisplayMemberPath = nameof(GistEditorFileDraft.Filename),
            SelectedItem = session.SelectedFile,
            SelectionMode = ListViewSelectionMode.Single
        };
        AutomationProperties.SetAutomationId(files, "GistEditorFiles");
        AutomationProperties.SetName(files, T("Gists/Editor/FilesAutomationName", "Gist files"));
        files.ContainerContentChanging += (_, args) =>
        {
            if (args.Item is GistEditorFileDraft draft && args.ItemContainer is ListViewItem container)
            {
                UpdateEditorFileContainerAutomation(container, draft, session.Files.IndexOf(draft));
            }
        };
        Grid.SetRow(files, 1);
        fileRail.Children.Add(files);

        StackPanel fileCommands = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
        Button addFile = CreateEditorCommandButton("GistEditorAddFile", T("Gists/Editor/AddFile", "Add file"), "\uE710");
        Button removeFile = CreateEditorCommandButton("GistEditorRemoveFile", T("Gists/Editor/RemoveFile", "Remove selected file"), "\uE74D");
        removeFile.IsEnabled = session.CanRemoveFile;
        fileCommands.Children.Add(addFile);
        fileCommands.Children.Add(removeFile);
        Grid.SetRow(fileCommands, 2);
        fileRail.Children.Add(fileCommands);
        filesArea.Children.Add(fileRail);

        Grid fileEditor = new() { RowSpacing = 8 };
        fileEditor.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        fileEditor.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        fileEditor.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        fileEditor.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        string fileNameText = T("Gists/Editor/FileName", "File name");
        string fileContentText = T("Gists/Editor/FileContent", "File content");
        TextBox filename = CreateEditorTextBox("GistEditorFilename", fileNameText, fileNameText, T("Gists/Editor/FileNamePlaceholder", "filename.ext"));
        TextBox content = CreateEditorTextBox("GistEditorContent", fileContentText, null, fileContentText);
        content.Style = (Style)Resources["GistEditorContentTextBoxStyle"];
        content.AcceptsReturn = true;
        content.MaxLength = GistFileRenderPolicy.MaximumPreviewCharacters;
        content.HorizontalAlignment = HorizontalAlignment.Stretch;
        content.VerticalAlignment = VerticalAlignment.Stretch;
        content.FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["AppMonoFontFamily"];
        content.FontSize = (double)Application.Current.Resources["AppFontSize13"];
        TextBlock oversizedStatus = new()
        {
            Text = T("Gists/Editor/TooLarge", "This file is too large to edit here. Its complete content is preserved; use Save as from the detail view to work with the full file."),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Style = (Style)Application.Current.Resources["SectionSecondaryTextBlockStyle"]
        };
        AutomationProperties.SetAutomationId(oversizedStatus, "GistEditorContentLimit");
        TextBlock contentLabel = new()
        {
            Text = fileContentText,
            FontSize = (double)Application.Current.Resources["AppFontSize13"],
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AppInkBrush"]
        };
        AutomationProperties.SetAutomationId(contentLabel, "GistEditorContentLabel");
        Grid.SetRow(contentLabel, 1);
        Grid.SetRow(oversizedStatus, 2);
        Grid.SetRow(content, 3);
        fileEditor.Children.Add(filename);
        fileEditor.Children.Add(contentLabel);
        fileEditor.Children.Add(oversizedStatus);
        fileEditor.Children.Add(content);
        Grid.SetColumn(fileEditor, 1);
        filesArea.Children.Add(fileEditor);
        root.Children.Add(filesArea);

        void UpdateResponsiveEditor(double width)
        {
            bool compact = width < 520;
            filesArea.ColumnDefinitions[0].Width = compact
                ? new GridLength(1, GridUnitType.Star)
                : new GridLength(188);
            filesArea.ColumnDefinitions[1].Width = compact
                ? new GridLength(0)
                : new GridLength(1, GridUnitType.Star);
            filesArea.RowDefinitions[0].Height = compact
                ? GridLength.Auto
                : new GridLength(1, GridUnitType.Star);
            filesArea.RowDefinitions[1].Height = compact
                ? new GridLength(1, GridUnitType.Star)
                : new GridLength(0);
            fileRail.Height = compact ? 112 : double.NaN;
            Grid.SetColumn(fileEditor, compact ? 0 : 1);
            Grid.SetRow(fileEditor, compact ? 1 : 0);
        }

        filesArea.SizeChanged += (_, args) => UpdateResponsiveEditor(args.NewSize.Width);
        UpdateResponsiveEditor(Math.Max(0, XamlRoot.Size.Width - 104));

        bool isProjectingSelection = false;
        GistEditorFileDraft? displayedDraft = null;
        void ProjectSelection()
        {
            isProjectingSelection = true;
            GistEditorFileDraft? selected = session.SelectedFile;
            displayedDraft = selected;
            filename.Text = selected?.Filename ?? string.Empty;
            GistFileRenderModel renderModel = GistFileRenderPolicy.Create(selected?.Content);
            content.Text = renderModel.PreviewText;
            bool cannotEditContent = selected is { IsContentAvailable: false } || renderModel.IsCapped;
            content.IsReadOnly = cannotEditContent;
            oversizedStatus.Text = selected is { IsContentAvailable: false }
                ? T("Gists/Editor/ContentUnavailable", "GitHub did not include this file's content. Its existing content will be preserved; load the full file before editing it.")
                : T("Gists/Editor/TooLarge", "This file is too large to edit here. Its complete content is preserved; use Save as from the detail view to work with the full file.");
            oversizedStatus.Visibility = cannotEditContent ? Visibility.Visible : Visibility.Collapsed;
            removeFile.IsEnabled = session.CanRemoveFile;
            UpdateRealizedFileContainerAutomation();
            isProjectingSelection = false;
        }

        void UpdateRealizedFileContainerAutomation()
        {
            for (int index = 0; index < session.Files.Count; index++)
            {
                GistEditorFileDraft draft = session.Files[index];
                if (files.ContainerFromItem(draft) is ListViewItem container)
                {
                    UpdateEditorFileContainerAutomation(container, draft, index);
                }
            }
        }

        void CommitCurrentEditorFields()
        {
            if (isProjectingSelection)
            {
                return;
            }

            session.CommitDisplayedFile(displayedDraft, filename.Text, content.Text, !content.IsReadOnly);
        }

        files.SelectionChanged += (_, _) =>
        {
            CommitCurrentEditorFields();
            session.SelectedFile = files.SelectedItem as GistEditorFileDraft;
            ProjectSelection();
        };
        filename.TextChanged += (_, _) =>
        {
            if (!isProjectingSelection && session.SelectedFile is { } selected)
            {
                selected.Filename = filename.Text;
                if (files.ContainerFromItem(selected) is ListViewItem container)
                {
                    UpdateEditorFileContainerAutomation(container, selected, session.Files.IndexOf(selected));
                }
            }
        };
        content.TextChanged += (_, _) =>
        {
            if (!isProjectingSelection && session.SelectedFile is { } selected)
            {
                selected.Content = content.Text;
            }
        };
        addFile.Click += (_, _) =>
        {
            CommitCurrentEditorFields();
            session.AddFile();
            files.SelectedItem = session.SelectedFile;
            files.ScrollIntoView(session.SelectedFile);
            ProjectSelection();
        };
        removeFile.Click += (_, _) =>
        {
            CommitCurrentEditorFields();
            session.RemoveSelectedFile();
            files.SelectedItem = session.SelectedFile;
            ProjectSelection();
        };
        ProjectSelection();
        return root;
    }

    private static void UpdateEditorFileContainerAutomation(
        ListViewItem container,
        GistEditorFileDraft draft,
        int index)
    {
        AutomationProperties.SetAutomationId(container, $"GistEditorFile_{Math.Max(index, 0)}");
        AutomationProperties.SetName(container, draft.AutomationName);
    }

    private static TextBox CreateEditorTextBox(string automationId, string automationName, string? header, string placeholder)
    {
        TextBox textBox = new()
        {
            Header = header,
            PlaceholderText = placeholder,
            Style = (Style)Application.Current.Resources["AppTextBoxStyle"]
        };
        AutomationProperties.SetAutomationId(textBox, automationId);
        AutomationProperties.SetName(textBox, automationName);
        return textBox;
    }

    private static Button CreateEditorCommandButton(string automationId, string automationName, string glyph)
    {
        Button button = new()
        {
            Width = 34,
            Height = 34,
            Style = (Style)Application.Current.Resources["AppToolbarButtonStyle"],
            Content = new FontIcon
            {
                FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["SegoeFluentIcons"],
                FontSize = (double)Application.Current.Resources["AppFontSize14"],
                Glyph = glyph
            }
        };
        AutomationProperties.SetAutomationId(button, automationId);
        AutomationProperties.SetName(button, automationName);
        ToolTipService.SetToolTip(button, automationName);
        return button;
    }

    private async void ViewModel_DeleteGistRequested(object? sender, EventArgs e)
    {
        if (ViewModel.SelectedGistItem is not { } selected)
        {
            return;
        }

        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = T("Gists/Delete/Title", "Delete gist?"),
            Content = TF("Gists/Delete/ContentFormat", "Delete '{0}' permanently? This cannot be undone.", selected.Title),
            PrimaryButtonText = T("Common/Delete", "Delete"),
            CloseButtonText = T("Common/Cancel", "Cancel"),
            DefaultButton = ContentDialogButton.Close,
            PrimaryButtonStyle = (Style)Application.Current.Resources["AppDestructiveButtonStyle"]
        };
        AppDialogStyleCatalog.Apply(dialog);
        AutomationProperties.SetAutomationId(dialog, "GistDeleteDialog");
        AutomationProperties.SetName(dialog, T("Gists/Delete/AutomationName", "Delete gist"));
        TextBlock errorText = AppContentDialogPresenter.CreateInlineErrorPresenter("GistDeleteDialogError");
        dialog.Content = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = TF("Gists/Delete/ContentFormat", "Delete '{0}' permanently? This cannot be undone.", selected.Title),
                    TextWrapping = TextWrapping.Wrap
                },
                errorText
            }
        };
        _activeDialog = dialog;
        ContentDialogResult result;
        try
        {
            result = await AppContentDialogPresenter.ShowForPrimaryActionAsync(
                dialog,
                XamlRoot,
                async () =>
                {
                    if (_pageCancellationTokenSource is not { } pageCancellationTokenSource)
                    {
                        return DialogMutationResult.Failure(T("Common/PageUnavailable", "This page is no longer available."));
                    }

                    bool deleted = await ViewModel.DeleteSelectedGistAsync(pageCancellationTokenSource.Token);
                    return deleted
                        ? DialogMutationResult.Success()
                        : DialogMutationResult.Failure(ViewModel.ErrorMessage);
                },
                errorText);
        }
        finally
        {
            if (ReferenceEquals(_activeDialog, dialog))
            {
                _activeDialog = null;
            }
        }

        _ = result;
    }

    private void ViewModel_ShareRequested(object? sender, EventArgs e)
    {
        if (ViewModel.SelectedGistItem is not { } selected)
        {
            return;
        }

        try
        {
            _shareItem = selected;
            _shareManager ??= DesktopDataTransferManagerHelper.GetForWindow(((App)Application.Current).CurrentMainWindow);
            _shareManager.DataRequested -= ShareManager_DataRequested;
            _shareManager.DataRequested += ShareManager_DataRequested;
            DesktopDataTransferManagerHelper.ShowShareUIForWindow(((App)Application.Current).CurrentMainWindow);
            ViewModel.TrackShareSuccess();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Could not open share UI: {ex}");
            ViewModel.ReportActionError(T("Gists/Error/ShareUnavailable", "Windows Share is temporarily unavailable. The Copy link action is still available."), "share");
        }
    }

    private void ViewModel_CopyRequested(object? sender, EventArgs e)
    {
        if (ViewModel.SelectedGistItem is not { Gist.HtmlUrl: { Length: > 0 } url })
        {
            return;
        }

        if (PlatformHelper.CopyString(url))
        {
            ViewModel.TrackCopySuccess();
            return;
        }

        ViewModel.ReportActionError(
            T("Gists/Error/CopyLink", "The gist link could not be copied."),
            "copy_link");
    }

    private void ViewModel_CopyFileRequested(object? sender, EventArgs e)
    {
        if (ViewModel.SelectedFile?.File is not { Truncated: false, Content: { } content })
        {
            return;
        }

        if (PlatformHelper.CopyString(content))
        {
            ViewModel.TrackCopyFileSuccess();
            return;
        }

        ViewModel.ReportActionError(
            T("Gists/Error/CopyFile", "The Gist file content could not be copied."),
            "copy_file");
    }

    private async void ViewModel_SaveFileRequested(object? sender, EventArgs e)
    {
        if (ViewModel.SelectedFile?.File is not { Truncated: false, Content: { } content } file)
        {
            return;
        }

        try
        {
            FileSavePicker picker = new()
            {
                SuggestedStartLocation = PickerLocationId.Downloads,
                SuggestedFileName = string.IsNullOrWhiteSpace(file.Filename) ? T("Gists/Save/DefaultFileName", "gist-file") : file.Filename
            };
            string extension = Path.GetExtension(file.Filename);
            if (string.IsNullOrWhiteSpace(extension) || extension.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                extension = ".txt";
            }

            picker.FileTypeChoices.Add(T("Gists/Save/FileType", "Gist file"), [extension]);
            InitializeWithWindow.Initialize(
                picker,
                WindowNative.GetWindowHandle(((App)Application.Current).CurrentMainWindow));
            StorageFile? destination = await picker.PickSaveFileAsync();
            if (destination is null)
            {
                return;
            }

            await FileIO.WriteTextAsync(destination, content);
            ViewModel.TrackActionSuccess("save_file");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Could not save Gist file content: {ex}");
            ViewModel.ReportActionError(T("Gists/Error/SaveFile", "The Gist file could not be saved."), "save_file");
        }
    }

    private void ShareManager_DataRequested(DataTransferManager sender, DataRequestedEventArgs args)
    {
        if (_shareItem is not { Gist.HtmlUrl: { Length: > 0 } url })
        {
            args.Request.FailWithDisplayText(T("Gists/Share/NoSelection", "No gist is selected."));
            return;
        }

        DataPackage data = args.Request.Data;
        data.Properties.Title = _shareItem.Title;
        data.Properties.Description = _shareItem.VisibilityText + " GitHub gist";
        data.SetWebLink(new Uri(url));
        data.SetText(url);
    }

    private void GistVisibilityFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        ViewModel.SetVisibilityFilter(GistVisibilityFilterComboBox.SelectedIndex switch
        {
            1 => GistVisibilityFilter.Public,
            2 => GistVisibilityFilter.Secret,
            _ => GistVisibilityFilter.All
        });
    }

    private void GistSort_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        ViewModel.SetSort(GistSortComboBox.SelectedIndex switch
        {
            1 => GistLibrarySort.Newest,
            2 => GistLibrarySort.Oldest,
            3 => GistLibrarySort.Title,
            _ => GistLibrarySort.RecentlyUpdated
        });
    }

    private void GistsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is GistViewItem item)
        {
            ViewModel.SelectedGistItem = item;
            if (GistsWorkspace.IsLeadingDrawerOpen)
            {
                GistsWorkspace.CloseDrawer();
            }
        }
    }

    private void GistsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ProductPerformanceReadiness.IsEnabled ||
            sender is not ListView { SelectedItem: GistViewItem item } ||
            string.Equals(_lastGistTraversalKey, item.StableKey, StringComparison.Ordinal))
        {
            return;
        }

        StartGistTraversal(item);
    }

    private void GistListItem_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not ListViewItem { Content: GistViewItem item } container ||
            e.GetCurrentPoint(container).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed)
        {
            return;
        }

        StartGistTraversal(item);
        GistsList.SelectedItem = item;
        ViewModel.SelectedGistItem = item;
    }

    private void StartGistTraversal(GistViewItem item)
    {
        if (!ProductPerformanceReadiness.IsEnabled)
        {
            return;
        }

        _lastGistTraversalKey = item.StableKey;
        ProductPerformanceReadiness.BeginTraversal(
            "gists",
            item.AutomationId,
            "gists");
        long generation = Interlocked.Increment(ref _gistTraversalGeneration);
        QueueGistTraversalCommit(item, generation, remainingAttempts: 6);
    }

    private void QueueGistTraversalCommit(
        GistViewItem item,
        long generation,
        int remainingAttempts)
    {
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (generation != Volatile.Read(ref _gistTraversalGeneration) ||
                !ReferenceEquals(ViewModel.SelectedGistItem, item))
            {
                return;
            }

            if (ViewModel.HasSelection &&
                string.Equals(GistsDetailTitleText.Text, item.Title, StringComparison.Ordinal) &&
                string.Equals(
                    GistsFilePreviewTextBox.Text ?? string.Empty,
                    ViewModel.SelectedFileContent ?? string.Empty,
                    StringComparison.Ordinal))
            {
                ProductPerformanceRenderCommitter.ScheduleAfterNextFrame(
                    this,
                    () => IsLoaded &&
                        generation == Volatile.Read(ref _gistTraversalGeneration) &&
                        ReferenceEquals(ViewModel.SelectedGistItem, item),
                    () =>
                        ViewModel.HasSelection &&
                        string.Equals(GistsDetailTitleText.Text, item.Title, StringComparison.Ordinal) &&
                        string.Equals(
                            GistsFilePreviewTextBox.Text ?? string.Empty,
                            ViewModel.SelectedFileContent ?? string.Empty,
                            StringComparison.Ordinal),
                    () => ProductPerformanceReadiness.CommitTraversal(
                        "gists",
                        item.AutomationId));
                return;
            }

            if (remainingAttempts > 1)
            {
                QueueGistTraversalCommit(item, generation, remainingAttempts - 1);
            }
        });
    }

    private void GistsList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        const int applicationKey = 0x5D;
        bool isShiftF10 = e.Key == VirtualKey.F10 &&
            (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & CoreVirtualKeyStates.Down) != 0;
        if ((int)e.Key != applicationKey && !isShiftF10)
        {
            return;
        }

        object? selectedItem = GistsList.SelectedItem ?? ViewModel.SelectedGistItem;
        if (selectedItem is not null &&
            GistsList.ContainerFromItem(selectedItem) is ListViewItem container &&
            container.ContextFlyout is FlyoutBase flyout)
        {
            flyout.ShowAt(container);
            e.Handled = true;
        }
    }

    private void GistsList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is not ListViewItem container)
        {
            return;
        }

        container.RemoveHandler(
            PointerPressedEvent,
            new PointerEventHandler(GistListItem_PointerPressed));
        if (args.InRecycleQueue)
        {
            return;
        }

        container.AddHandler(
            PointerPressedEvent,
            new PointerEventHandler(GistListItem_PointerPressed),
            handledEventsToo: true);
        if (args.Item is GistViewItem item)
        {
            AutomationProperties.SetAutomationId(container, item.AutomationId);
            AutomationProperties.SetName(container, item.AutomationName);
            container.ContextFlyout = CreateGistRowContextFlyout(item);
        }
    }

    private MenuFlyout CreateGistRowContextFlyout(GistViewItem item)
    {
        MenuFlyout menu = new();
        menu.Opening += (_, _) => ViewModel.SelectedGistItem = item;
        menu.Items.Add(CreateMenuItem("Open", T("Common/Open", "Open"), "\uE8A7", () => ViewModel.SelectedGistItem = item));
        menu.Items.Add(CreateMenuItem("Edit", T("Common/Edit", "Edit"), "\uE70F", () => ViewModel.EditGistCommand.Execute(null)));
        menu.Items.Add(CreateMenuItem("CopyLink", T("Common/CopyLink", "Copy link"), "\uE8C8", () => ViewModel.CopyLinkCommand.Execute(null)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(CreateMenuItem("Delete", T("Common/Delete", "Delete"), "\uE74D", () => ViewModel.DeleteGistCommand.Execute(null)));
        return menu;
    }

    private static MenuFlyoutItem CreateMenuItem(string automationSuffix, string text, string glyph, Action action)
    {
        MenuFlyoutItem item = new()
        {
            Text = text,
            Icon = new FontIcon { FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["SegoeFluentIcons"], Glyph = glyph }
        };
        AutomationProperties.SetAutomationId(item, $"GistsContext{automationSuffix}");
        AutomationProperties.SetName(item, text);
        item.Click += (_, _) => action();
        return item;
    }

    private void GistsWorkspace_ModeChanged(object? sender, AdaptiveWorkspaceState state)
    {
        if (state.ShouldShowLeadingPaneButton && ViewModel.SelectedGistItem is null && !GistsWorkspace.IsLeadingDrawerOpen)
        {
            GistsWorkspace.OpenLeadingPane();
        }
    }

    private static string T(string key, string fallback) =>
        LocalizedResourceText.GetString(key, fallback);

    private static string TF(string key, string fallback, params object?[] arguments) =>
        LocalizedResourceText.Format(key, fallback, arguments);
}

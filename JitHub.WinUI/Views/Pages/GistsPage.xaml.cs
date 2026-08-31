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
using Microsoft.Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;
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

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UiTaskGuard.Run(async () =>
        {
            ApplyResponsiveLayout(ActualWidth);
            AttachPerformanceScrollProbe();
            await AwaitLatestStopAsync();
            if (_initialized)
            {
                return;
            }

            _pageCancellationTokenSource?.Dispose();
            CancellationTokenSource pageCancellationTokenSource = new();
            _pageCancellationTokenSource = pageCancellationTokenSource;
            SubscribeViewModelEvents();
            _initialized = true;
            try
            {
                await ViewModel.InitializeAsync(pageCancellationTokenSource.Token);
                ProductPerformanceReadiness.CommitRoute("gists", ProductPerformanceReadiness.CountIdentity(ViewModel.Gists.Count));
            }
            catch (OperationCanceledException) when (pageCancellationTokenSource.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                JitHub.WinUI.App.LogHandledException(ex, "ui-gists-page-initialize");
            }
        }, "ui-gists-page");
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        UiTaskGuard.Run(async () =>
        {
            Interlocked.Increment(ref _gistTraversalGeneration);
            _performanceScrollProbe?.Dispose();
            _performanceScrollProbe = null;
            UnsubscribeViewModelEvents();
            CancellationTokenSource? pageCancellationTokenSource = Interlocked.Exchange(ref _pageCancellationTokenSource, null);
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
                JitHub.WinUI.App.LogHandledException(ex, "ui-gists-page-stop");
            }
        }, "ui-gists-page");
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
                JitHub.WinUI.App.LogHandledException(ex, "ui-gists-page-prior-stop");
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

    private void ViewModel_NewGistRequested(object? sender, EventArgs e)
    {
        UiTaskGuard.Run(async () =>
        {
            if (_activeDialog is not null)
            {
                return;
            }

            GistEditorSession session = ViewModel.CreateNewEditorSession();
            await ShowEditorAsync(session, ViewModel.CreateGistAsync);
        }, "ui-gists-page");
    }

    private void ViewModel_EditGistRequested(object? sender, EventArgs e)
    {
        UiTaskGuard.Run(async () =>
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
        }, "ui-gists-page");
    }

    private async Task ShowEditorAsync(
        GistEditorSession session,
        Func<GistEditorSession, System.Threading.CancellationToken, Task<bool>> saveAsync)
    {
        FrameworkElement editor = CreateEditor(session);
        editor.HorizontalAlignment = HorizontalAlignment.Stretch;
        editor.VerticalAlignment = VerticalAlignment.Stretch;
        TextBlock errorText = AppContentDialogPresenter.CreateInlineErrorPresenter("GistEditorDialogError");
        AppDialogScrollableContent dialogContent = new()
        {
            RowSpacing = AppResource<double>("AppGap12"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        dialogContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        dialogContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        dialogContent.Children.Add(editor);
        Grid.SetRow(errorText, 1);
        dialogContent.Children.Add(errorText);
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = session.Title,
            Content = dialogContent,
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
            MinHeight = 0,
            RowSpacing = AppResource<double>("AppGap16"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        Grid metadata = new()
        {
            ColumnSpacing = AppResource<double>("AppGap16"),
            RowSpacing = AppResource<double>("AppGap12")
        };
        metadata.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        metadata.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(AppResource<double>("AppGistEditorVisibilityWidth")) });
        metadata.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        metadata.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0) });

        string descriptionText = T("Gists/Editor/Description", "Description");
        TextBox description = CreateEditorTextBox(
            "GistEditorDescription",
            T("Gists/Editor/DescriptionAutomationName", "Gist description"),
            descriptionText,
            T("Gists/Editor/OptionalDescription", "Optional description"));
        description.MaxLength = 256;
        description.Text = session.Description;
        description.HorizontalAlignment = HorizontalAlignment.Stretch;
        description.TextChanged += (_, _) => session.Description = description.Text;
        metadata.Children.Add(description);
        string visibilityText = T("Gists/Editor/Visibility", "Visibility");
        ToggleSwitch visibility = new()
        {
            Header = visibilityText,
            OffContent = T("Gists/Editor/Secret", "Secret"),
            OnContent = T("Gists/Editor/Public", "Public"),
            IsEnabled = session.CanChangeVisibility,
            IsOn = session.IsPublic,
            MinWidth = 0,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom
        };
        AutomationProperties.SetAutomationId(visibility, "GistEditorVisibility");
        AutomationProperties.SetName(visibility, T("Gists/Editor/VisibilityAutomationName", "Gist visibility"));
        visibility.Toggled += (_, _) => session.IsPublic = visibility.IsOn;
        Grid.SetColumn(visibility, 1);
        metadata.Children.Add(visibility);
        root.Children.Add(metadata);

        Grid filesArea = new();
        filesArea.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(AppResource<double>("AppGistEditorFileRailWidth")) });
        filesArea.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        filesArea.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        filesArea.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0) });

        Grid fileRail = new()
        {
            Padding = AppResource<Thickness>("AppPadding12"),
            RowSpacing = AppResource<double>("AppGap8")
        };
        fileRail.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        fileRail.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid fileRailHeader = new()
        {
            ColumnSpacing = AppResource<double>("AppGap4"),
            VerticalAlignment = VerticalAlignment.Center
        };
        fileRailHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        fileRailHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        fileRailHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        TextBlock filesLabel = CreateEditorLabel(T("Gists/Editor/Files", "Files"));
        filesLabel.VerticalAlignment = VerticalAlignment.Center;
        fileRailHeader.Children.Add(filesLabel);

        Button addFile = CreateEditorCommandButton("GistEditorAddFile", T("Gists/Editor/AddFile", "Add file"), "\uE710");
        Button removeFile = CreateEditorCommandButton("GistEditorRemoveFile", T("Gists/Editor/RemoveFile", "Remove selected file"), "\uE74D");
        removeFile.IsEnabled = session.CanRemoveFile;
        Grid.SetColumn(addFile, 1);
        Grid.SetColumn(removeFile, 2);
        fileRailHeader.Children.Add(addFile);
        fileRailHeader.Children.Add(removeFile);
        fileRail.Children.Add(fileRailHeader);

        ListView files = new()
        {
            ItemsSource = session.Files,
            ItemTemplate = (DataTemplate)Resources["GistEditorFileTemplate"],
            ItemContainerStyle = AppResource<Style>("AppDialogFileListRowStyle"),
            SelectedItem = session.SelectedFile,
            SelectionMode = ListViewSelectionMode.Single,
            Background = AppResource<Brush>("AppTransparentBrush"),
            BorderThickness = AppResource<Thickness>("AppZeroThickness"),
            Padding = AppResource<Thickness>("AppPadding0"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
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

        Border fileRailSurface = new()
        {
            Background = AppResource<Brush>("AppSurfaceSubtleBrush"),
            BorderBrush = AppResource<Brush>("AppOutlineBrush"),
            BorderThickness = AppResource<Thickness>("AppRightHairlineBorderThickness"),
            Child = fileRail
        };
        AutomationProperties.SetAutomationId(fileRailSurface, "GistEditorFileRail");
        filesArea.Children.Add(fileRailSurface);

        Grid fileEditor = new()
        {
            Padding = AppResource<Thickness>("AppPadding16"),
            RowSpacing = AppResource<double>("AppGap8")
        };
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
            FontFamily = AppResource<FontFamily>("AppUiFontFamily"),
            FontSize = AppResource<double>("AppFontSize13"),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = AppResource<Brush>("AppInkBrush"),
            VerticalAlignment = VerticalAlignment.Center
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

        Border editorFrame = new()
        {
            Background = AppResource<Brush>("AppOverlayBrush"),
            BorderBrush = AppResource<Brush>("AppOutlineBrush"),
            BorderThickness = AppResource<Thickness>("AppHairlineBorderThickness"),
            CornerRadius = AppResource<CornerRadius>("AppRadiusMedium"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = filesArea
        };
        AutomationProperties.SetAutomationId(editorFrame, "GistEditorWorkspace");
        Grid.SetRow(editorFrame, 1);
        root.Children.Add(editorFrame);

        void UpdateResponsiveEditor(double width)
        {
            bool shortWindow = XamlRoot.Size.Height < AppResource<double>("AppDialogCompactBreakpoint");
            bool compactMetadata = width < AppResource<double>("AppGistEditorCompactBreakpoint") && !shortWindow;
            bool stackedFiles = width < AppResource<double>("AppGistEditorStackedFileBreakpoint");
            metadata.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            metadata.ColumnDefinitions[1].Width = compactMetadata
                ? new GridLength(0)
                : new GridLength(AppResource<double>("AppGistEditorVisibilityWidth"));
            metadata.RowDefinitions[1].Height = compactMetadata
                ? GridLength.Auto
                : new GridLength(0);
            Grid.SetColumn(description, 0);
            Grid.SetColumn(visibility, compactMetadata ? 0 : 1);
            Grid.SetRow(visibility, compactMetadata ? 1 : 0);
            filesArea.ColumnDefinitions[0].Width = stackedFiles
                ? new GridLength(1, GridUnitType.Star)
                : new GridLength(AppResource<double>("AppGistEditorFileRailWidth"));
            filesArea.ColumnDefinitions[1].Width = stackedFiles
                ? new GridLength(0)
                : new GridLength(1, GridUnitType.Star);
            filesArea.RowDefinitions[0].Height = stackedFiles
                ? GridLength.Auto
                : new GridLength(1, GridUnitType.Star);
            filesArea.RowDefinitions[1].Height = stackedFiles
                ? new GridLength(1, GridUnitType.Star)
                : new GridLength(0);
            fileRailSurface.Height = stackedFiles
                ? AppResource<double>("AppGistEditorCompactFileRailHeight")
                : double.NaN;
            fileRailSurface.BorderThickness = stackedFiles
                ? AppResource<Thickness>("AppBottomHairlineBorderThickness")
                : AppResource<Thickness>("AppRightHairlineBorderThickness");
            fileEditor.Padding = compactMetadata
                ? AppResource<Thickness>("AppPadding12")
                : AppResource<Thickness>("AppPadding16");
            Grid.SetColumn(fileRailSurface, 0);
            Grid.SetRow(fileRailSurface, 0);
            Grid.SetColumn(fileEditor, stackedFiles ? 0 : 1);
            Grid.SetRow(fileEditor, stackedFiles ? 1 : 0);
            content.MinHeight = shortWindow
                ? AppResource<double>("AppGistEditorCompactContentMinHeight")
                : XamlRoot.Size.Height < AppResource<double>("AppDialogEditorPreferredHeight")
                    ? AppResource<double>("AppDimension120")
                    : AppResource<double>("AppDimension180");
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

    private static TextBlock CreateEditorLabel(string text) => new()
    {
        Text = text,
        FontFamily = AppResource<FontFamily>("AppUiFontFamily"),
        FontSize = AppResource<double>("AppFontSize13"),
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Foreground = AppResource<Brush>("AppInkBrush")
    };

    private static T AppResource<T>(string resourceKey)
    {
        if (Application.Current.Resources.TryGetValue(resourceKey, out object? value) && value is T typedValue)
        {
            return typedValue;
        }

        throw new InvalidOperationException($"Required app resource '{resourceKey}' is unavailable.");
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
            Width = AppResource<double>("AppCommandControlHeight"),
            Height = AppResource<double>("AppCommandControlHeight"),
            Style = AppResource<Style>("AppToolbarButtonStyle"),
            Content = new FontIcon
            {
                FontFamily = AppResource<FontFamily>("SegoeFluentIcons"),
                FontSize = AppResource<double>("AppFontSize14"),
                Glyph = glyph
            }
        };
        AutomationProperties.SetAutomationId(button, automationId);
        AutomationProperties.SetName(button, automationName);
        ToolTipService.SetToolTip(button, automationName);
        return button;
    }

    private void ViewModel_DeleteGistRequested(object? sender, EventArgs e)
    {
        UiTaskGuard.Run(async () =>
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
                result = await AppContentDialogPresenter.ShowForPrimaryActionAsync(dialog, XamlRoot, async () =>
                {
                    if (_pageCancellationTokenSource is not { } pageCancellationTokenSource)
                    {
                        return DialogMutationResult.Failure(T("Common/PageUnavailable", "This page is no longer available."));
                    }

                    bool deleted = await ViewModel.DeleteSelectedGistAsync(pageCancellationTokenSource.Token);
                    return deleted ? DialogMutationResult.Success() : DialogMutationResult.Failure(ViewModel.ErrorMessage);
                }, errorText, layoutKind: AppDialogLayoutKind.Confirmation);
            }
            finally
            {
                if (ReferenceEquals(_activeDialog, dialog))
                {
                    _activeDialog = null;
                }
            }

            _ = result;
        }, "ui-gists-page");
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
            JitHub.WinUI.App.LogHandledException(ex, "ui-gists-share");
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

    private void ViewModel_SaveFileRequested(object? sender, EventArgs e)
    {
        UiTaskGuard.Run(async () =>
        {
            if (ViewModel.SelectedFile?.File is not { Truncated: false, Content: { } content } file)
            {
                return;
            }

            try
            {
                FileSavePicker picker = new(((App)Application.Current).CurrentMainWindow.AppWindow.Id)
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
                PickFileResult? destination = await picker.PickSaveFileAsync();
                if (destination is null)
                {
                    return;
                }

                await File.WriteAllTextAsync(destination.Path, content);
                ViewModel.TrackActionSuccess("save_file");
            }
            catch (Exception ex)
            {
                JitHub.WinUI.App.LogHandledException(ex, "ui-gists-save-file");
                ViewModel.ReportActionError(T("Gists/Error/SaveFile", "The Gist file could not be saved."), "save_file");
            }
        }, "ui-gists-page");
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

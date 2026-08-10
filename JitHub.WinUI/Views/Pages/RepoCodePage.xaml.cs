using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.NavArgs;
using JitHub.Services;
using JitHub.Services.CodeViewer;
using JitHub.Services.Layout;
using JitHub.WinUI.Helpers;
using JitHub.WinUI.ViewModels.CodeViewer;
using JitHub.WinUI.Views.Controls.CodeViewer;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;
using Windows.UI.Core;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace JitHub.WinUI.Views.Pages;

public sealed partial class RepoCodePage : Page, IRepositoryCompactCommandProvider
{
    private readonly App _app = (App)Application.Current;
    private CancellationTokenSource? _initCts;
    private DispatcherQueueTimer? _performanceHeartbeatTimer;
    private long _performanceHeartbeat;
    private long _navigationGeneration;

    public RepoCodePageViewModel ViewModel { get; }

    public RepoCodePage()
    {
        ViewModel = _app.GetService<RepoCodePageViewModel>();
        InitializeComponent();
        ProductPerformanceReadiness.RecordTraversalStage("repo_code.xaml.ready");
        PreviewHost.ActionExecuted += ViewModel.TrackAction;
        FileTree.TabNavigationRequested += FileTree_TabNavigationRequested;
        CloseFileTreeButton.AddHandler(
            PreviewKeyDownEvent,
            new KeyEventHandler(DrawerTabStop_PreviewKeyDown),
            handledEventsToo: true);
        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ProductPerformanceReadiness.RecordTraversalStage("repo_code.navigated");

        if (e.Parameter is not CodeViewerNavArg arg || arg.Repo is null)
        {
            ShowError(LocalizedResourceText.GetString(
                "RepoCode/Error/MissingContext",
                "Repository context is required to open the code viewer."));
            return;
        }

        var repo = arg.Repo;
        var owner = repo.Owner?.Login;
        var name = repo.Name;
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(name))
        {
            ShowError(LocalizedResourceText.GetString(
                "RepoCode/Error/IncompleteMetadata",
                "Repository metadata is incomplete."));
            return;
        }

        string? gitRef = arg.IsBranch ? arg.Branch : arg.GitRef;
        if (string.IsNullOrWhiteSpace(gitRef))
        {
            gitRef = repo.DefaultBranch;
        }

        if (string.IsNullOrWhiteSpace(gitRef))
        {
            ShowError(LocalizedResourceText.GetString(
                "RepoCode/Error/MissingRef",
                "Could not determine which branch to load."));
            return;
        }

        _initCts?.Cancel();
        _initCts?.Dispose();
        _initCts = new CancellationTokenSource();
        long navigationGeneration = Interlocked.Increment(ref _navigationGeneration);
        CancellationToken routeToken = _initCts.Token;

        try
        {
            // Publish the stable workspace state, then let it paint before a
            // ready preparation can reconcile synchronously. Without this
            // boundary a cache hit can be slower perceptually than a miss.
            ProductPerformanceReadiness.CommitRoute(
                "repo_code",
                $"repo={owner}/{name};state=visible");
            ProductPerformanceReadiness.RecordTraversalStage("repo_code.visible");
            await WaitForRenderedFrameAsync(routeToken);
            if (navigationGeneration != Volatile.Read(ref _navigationGeneration))
            {
                return;
            }

            await ViewModel.InitializeAsync(owner!, name!, gitRef!, routeToken);
            if (navigationGeneration != Volatile.Read(ref _navigationGeneration))
            {
                return;
            }

            ProductPerformanceReadiness.CommitRoute(
                "repo_code",
                $"repo={owner}/{name};{ProductPerformanceReadiness.CountIdentity(ViewModel.Tree.RootNodes.Count)}");
            UpdatePaneButtonVisibility();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ShowError(JitHub.WinUI.Helpers.UserFacingError.For(
                ex,
                JitHub.WinUI.Helpers.UserFacingErrorKind.Loading,
                "repository-code"));
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        Interlocked.Increment(ref _navigationGeneration);
        _initCts?.Cancel();
        _initCts?.Dispose();
        _initCts = null;
        ViewModel.CancelPendingRequests();
        base.OnNavigatedFrom(e);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RepoCodePageViewModel.LoadError))
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!string.IsNullOrEmpty(ViewModel.LoadError))
                {
                    ShowError(ViewModel.LoadError!);
                }
                else
                {
                    ErrorBanner.IsOpen = false;
                }
            });
        }
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        StartPerformanceHeartbeatIfRequested();
        DispatcherQueue.TryEnqueue(UpdatePaneButtonVisibility);
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        StopPerformanceHeartbeat();
    }

    private void CodeWorkspace_ModeChanged(object? sender, AdaptiveWorkspaceState e)
        => UpdatePaneButtonVisibility();

    private void Breadcrumb_FileTreeRequested(object? sender, EventArgs e)
    {
        ViewModel.TrackAction(RepoCodeTelemetryActions.Drawer);
        CodeWorkspace.OpenLeadingPane();
    }

    private void CloseFileTreeButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.TrackAction(RepoCodeTelemetryActions.Drawer);
        CodeWorkspace.CloseDrawer();
    }

    private void FileTree_FileInvoked(object? sender, EventArgs e)
    {
        if (CodeWorkspace.IsLeadingDrawerOpen)
        {
            CodeWorkspace.CloseDrawer();
        }
    }

    private static async Task WaitForRenderedFrameAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TaskCompletionSource<bool> rendered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<object>? handler = null;
        handler = (_, _) =>
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= handler;
            rendered.TrySetResult(true);
        };

        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += handler;
        using CancellationTokenRegistration registration = cancellationToken.Register(
            () => rendered.TrySetCanceled(cancellationToken));
        try
        {
            await rendered.Task;
        }
        finally
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= handler;
        }
    }

    private void FileTree_TabNavigationRequested(
        object? sender,
        RepoFileTreeTabNavigationEventArgs e)
        => e.Handled = CodeWorkspace.TryMoveFocusWithinOpenDrawer(e.MoveBackward);

    private void DrawerTabStop_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Tab)
        {
            return;
        }

        CoreVirtualKeyStates shiftState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
        bool moveBackward = (shiftState & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
        e.Handled = CodeWorkspace.TryMoveFocusWithinOpenDrawer(moveBackward);
    }

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool control = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) != 0;
        bool shift = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & CoreVirtualKeyStates.Down) != 0;

        if (e.Key == VirtualKey.F6)
        {
            e.Handled = true;
            MoveWorkspaceFocus();
        }
        else if (control && shift && e.Key == VirtualKey.E)
        {
            e.Handled = true;
            FocusFileTree();
        }
        else if (control && e.Key == VirtualKey.F && PreviewHost.OpenFind())
        {
            e.Handled = true;
        }
    }

    private void MoveWorkspaceFocus()
    {
        DependencyObject? focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        if (IsDescendantOf(focused, FileTree))
        {
            if (CodeWorkspace.IsLeadingDrawerOpen) CodeWorkspace.CloseDrawer();
            PreviewHost.FocusPrimary();
            return;
        }

        FocusFileTree();
    }

    private void FocusFileTree()
    {
        if (CodeWorkspace.State?.LeadingPanePlacement != AdaptivePanePlacement.Inline)
        {
            CodeWorkspace.OpenLeadingPane();
            DispatcherQueue.TryEnqueue(() => FileTree.FocusTree());
        }
        else
        {
            FileTree.FocusTree();
        }
    }

    private static bool IsDescendantOf(DependencyObject? element, DependencyObject ancestor)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor)) return true;
        }

        return false;
    }

    private void UpdatePaneButtonVisibility()
    {
        AdaptiveWorkspaceState? state = CodeWorkspace.State;
        bool isLeadingDrawerOpen = state?.VisibleDrawer == AdaptiveWorkspaceDrawer.Leading;
        Breadcrumb.ShowFileTreeButton = state?.ShouldShowLeadingPaneButton == true && !isLeadingDrawerOpen;
        FileTreeDrawerHeader.Visibility = isLeadingDrawerOpen
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ShowError(string message)
    {
        ErrorBanner.Message = message;
        ErrorBanner.IsOpen = true;
    }

    private void StartPerformanceHeartbeatIfRequested()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("JITHUB_PREVIEW_SCENARIO"),
                "repo-code-performance",
                StringComparison.OrdinalIgnoreCase) ||
            _performanceHeartbeatTimer is not null)
        {
            return;
        }

        DispatcherQueueTimer timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(16);
        timer.IsRepeating = true;
        timer.Tick += PerformanceHeartbeatTimer_Tick;
        _performanceHeartbeatTimer = timer;
        PerformanceHeartbeatTimer_Tick(timer, EventArgs.Empty);
        timer.Start();
    }

    private void PerformanceHeartbeatTimer_Tick(DispatcherQueueTimer sender, object args)
        => AutomationProperties.SetItemStatus(RootGrid, $"heartbeat:{++_performanceHeartbeat}");

    private void StopPerformanceHeartbeat()
    {
        if (_performanceHeartbeatTimer is not { } timer) return;
        timer.Stop();
        timer.Tick -= PerformanceHeartbeatTimer_Tick;
        _performanceHeartbeatTimer = null;
    }

    public System.Collections.Generic.IReadOnlyList<RepositoryCompactCommand> GetRepositoryCompactCommands() =>
    [
        new(
            "file-tree",
            LocalizedResourceText.GetString("RepoCode/Command/ShowFileTree", "Show file tree"),
            () =>
            {
                ViewModel.TrackAction(RepoCodeTelemetryActions.Drawer);
                CodeWorkspace.OpenLeadingPane();
                DispatcherQueue.TryEnqueue(() => FileTree.FocusTree());
            })
    ];
}

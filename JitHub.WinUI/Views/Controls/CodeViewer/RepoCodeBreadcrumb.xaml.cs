using System;
using System.Windows.Input;
using JitHub.Services.Layout;
using JitHub.WinUI.ViewModels.CodeViewer;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.WinUI.Views.Controls.CodeViewer;

/// <summary>
/// Breadcrumb + action bar for the native code viewer.
/// DataContext must be set to a <see cref="RepoCodeBreadcrumbViewModel"/> by the owner.
/// Wire <see cref="GoBackCommand"/>, <see cref="GoForwardCommand"/>,
/// <see cref="CanGoBack"/>, and <see cref="CanGoForward"/> from
/// <c>RepoCodePageViewModel</c> in the page.
/// </summary>
[WinRT.GeneratedBindableCustomProperty]
public sealed partial class RepoCodeBreadcrumb : UserControl
{
    // ── Dependency Properties ─────────────────────────────────────────────

    public static readonly DependencyProperty GoBackCommandProperty =
        DependencyProperty.Register(
            nameof(GoBackCommand), typeof(ICommand),
            typeof(RepoCodeBreadcrumb), new PropertyMetadata(null));

    public static readonly DependencyProperty GoForwardCommandProperty =
        DependencyProperty.Register(
            nameof(GoForwardCommand), typeof(ICommand),
            typeof(RepoCodeBreadcrumb), new PropertyMetadata(null));

    public static readonly DependencyProperty CanGoBackProperty =
        DependencyProperty.Register(
            nameof(CanGoBack), typeof(bool),
            typeof(RepoCodeBreadcrumb), new PropertyMetadata(false));

    public static readonly DependencyProperty CanGoForwardProperty =
        DependencyProperty.Register(
            nameof(CanGoForward), typeof(bool),
            typeof(RepoCodeBreadcrumb), new PropertyMetadata(false));

    public static readonly DependencyProperty ShowFileTreeButtonProperty =
        DependencyProperty.Register(
            nameof(ShowFileTreeButton), typeof(bool),
            typeof(RepoCodeBreadcrumb), new PropertyMetadata(false, OnShowFileTreeButtonChanged));

    public ICommand? GoBackCommand
    {
        get => (ICommand?)GetValue(GoBackCommandProperty);
        set => SetValue(GoBackCommandProperty, value);
    }

    public ICommand? GoForwardCommand
    {
        get => (ICommand?)GetValue(GoForwardCommandProperty);
        set => SetValue(GoForwardCommandProperty, value);
    }

    public bool CanGoBack
    {
        get => (bool)GetValue(CanGoBackProperty);
        set => SetValue(CanGoBackProperty, value);
    }

    public bool CanGoForward
    {
        get => (bool)GetValue(CanGoForwardProperty);
        set => SetValue(CanGoForwardProperty, value);
    }

    public bool ShowFileTreeButton
    {
        get => (bool)GetValue(ShowFileTreeButtonProperty);
        set => SetValue(ShowFileTreeButtonProperty, value);
    }

    // ── Constructor ───────────────────────────────────────────────────────

    public RepoCodeBreadcrumb()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ApplyResponsiveLayout(ActualWidth);
    }

    public event EventHandler? FileTreeRequested;

    // Typed accessor for x:Bind expressions on ViewModel members.
    // Private is fine — x:Bind generates code in the same partial class.
    private RepoCodeBreadcrumbViewModel? ViewModel => DataContext as RepoCodeBreadcrumbViewModel;

    private RepoCodeBreadcrumbViewModel? _subscribedViewModel;

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _subscribedViewModel = null;
        }

        if (ViewModel is { } vm)
        {
            _subscribedViewModel = vm;
            vm.PropertyChanged += OnViewModelPropertyChanged;
        }

        // Re-evaluate all x:Bind expressions whenever the DataContext is replaced.
        Bindings.Update();
        ApplyResponsiveLayout(ActualWidth);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_subscribedViewModel is null && ViewModel is { } viewModel)
        {
            _subscribedViewModel = viewModel;
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
            Bindings.Update();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _subscribedViewModel = null;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RepoCodeBreadcrumbViewModel.IsCopyPathDone)
            or nameof(RepoCodeBreadcrumbViewModel.IsCopyRawUrlDone))
        {
            Bindings.Update();
        }
        else if (e.PropertyName is nameof(RepoCodeBreadcrumbViewModel.IsPathTransitioning)
            or nameof(RepoCodeBreadcrumbViewModel.CurrentPath))
        {
            ApplyResponsiveLayout(ActualWidth);
        }
    }

    private void OnBreadcrumbSegmentClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: BreadcrumbSegment segment })
        {
            ViewModel?.NavigateToSegmentCommand.Execute(segment);
        }
    }

    private static void OnShowFileTreeButtonChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
        => ((RepoCodeBreadcrumb)dependencyObject).UpdateFileTreeButtonVisibility();

    private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
        => ApplyResponsiveLayout(e.NewSize.Width);

    private void OpenFileTreeButton_Click(object sender, RoutedEventArgs e)
        => FileTreeRequested?.Invoke(this, EventArgs.Empty);

    private void FileActionsOverflowButton_Click(object sender, RoutedEventArgs e)
    {
        if (!FileActionsOverflowFlyout.IsOpen)
        {
            FileActionsOverflowFlyout.ShowAt(FileActionsOverflowButton);
        }
    }

    private void ApplyResponsiveLayout(double availableWidth)
    {
        RepoCodeBreadcrumbState state = RepoCodeResponsiveLayout.CalculateBreadcrumb(availableWidth);
        bool isPathTransitioning = ViewModel?.IsPathTransitioning == true;
        FullBreadcrumbHost.Visibility = state.ShowFullPath && !isPathTransitioning
            ? Visibility.Visible
            : Visibility.Collapsed;
        TransitionPathText.Visibility = state.ShowFullPath && isPathTransitioning
            ? Visibility.Visible
            : Visibility.Collapsed;
        DirectActionsHost.Visibility = state.ShowDirectActions ? Visibility.Visible : Visibility.Collapsed;
        CompactFileName.Visibility = state.ShowFileName ? Visibility.Visible : Visibility.Collapsed;
        FileActionsOverflowButton.Visibility = state.ShowActionsOverflow ? Visibility.Visible : Visibility.Collapsed;
        UpdateFileTreeButtonVisibility();
    }

    private void UpdateFileTreeButtonVisibility()
        => OpenFileTreeButton.Visibility = ShowFileTreeButton
            ? Visibility.Visible
            : Visibility.Collapsed;

    // ── Static helper for DataTemplate x:Bind expressions ────────────────

    /// <summary>
    /// Returns <see cref="Visibility.Visible"/> when the segment is NOT the root,
    /// so the "›" separator is shown between path segments.
    /// </summary>
    public static Visibility NotRootVis(bool isRoot)
        => isRoot ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Returns checkmark glyph when done, copy glyph otherwise.</summary>
    public static string CopyPathGlyph(bool done) => done ? "\uE10B" : "\uE8C8";

    /// <summary>Returns checkmark glyph when done, link glyph otherwise.</summary>
    public static string CopyRawUrlGlyph(bool done) => done ? "\uE10B" : "\uE71B";
}

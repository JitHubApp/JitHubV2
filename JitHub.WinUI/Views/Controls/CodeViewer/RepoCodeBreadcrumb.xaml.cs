using System.Windows.Input;
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

    // ── Constructor ───────────────────────────────────────────────────────

    public RepoCodeBreadcrumb()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    // Typed accessor for x:Bind expressions on ViewModel members.
    // Private is fine — x:Bind generates code in the same partial class.
    private RepoCodeBreadcrumbViewModel? ViewModel => DataContext as RepoCodeBreadcrumbViewModel;

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        // Re-evaluate all x:Bind expressions whenever the DataContext is replaced.
        Bindings.Update();
    }

    // ── Static helper for DataTemplate x:Bind expressions ────────────────

    /// <summary>
    /// Returns <see cref="Visibility.Visible"/> when the segment is NOT the root,
    /// so the "›" separator is shown between path segments.
    /// </summary>
    public static Visibility NotRootVis(bool isRoot)
        => isRoot ? Visibility.Collapsed : Visibility.Visible;
}

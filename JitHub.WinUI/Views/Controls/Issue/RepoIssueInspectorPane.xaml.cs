using System;
using JitHub.WinUI.ViewModels.Pages;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.WinUI.Views.Controls.Issue;

public sealed partial class RepoIssueInspectorPane : UserControl
{
    public RepoIssuePageViewModel ViewModel { get; }

    public event EventHandler? CloseRequested;
    public event EventHandler? MetadataRequested;
    public event EventHandler? ReactionsRequested;

    public RepoIssueInspectorPane(RepoIssuePageViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public void SetDrawerOpen(bool isOpen) =>
        CloseInspectorPaneButton.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;

    private void CloseInspectorPaneButton_Click(object sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(this, EventArgs.Empty);

    private void MetadataButton_Click(object sender, RoutedEventArgs e) =>
        MetadataRequested?.Invoke(this, EventArgs.Empty);

    private void ReactionsButton_Click(object sender, RoutedEventArgs e) =>
        ReactionsRequested?.Invoke(this, EventArgs.Empty);
}

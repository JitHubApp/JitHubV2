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

    public RepoIssueInspectorPane(RepoIssuePageViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public void UpdateResponsiveState(bool isDrawerOpen, bool isCompactWorkspace)
    {
        CloseInspectorPaneButton.Visibility = isDrawerOpen ? Visibility.Visible : Visibility.Collapsed;
        InspectorPanel.Padding = isCompactWorkspace
            ? new Thickness(16, 10, 10, 16)
            : new Thickness(16, 10, 16, 16);
    }

    private void CloseInspectorPaneButton_Click(object sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(this, EventArgs.Empty);

    private void MetadataButton_Click(object sender, RoutedEventArgs e) =>
        MetadataRequested?.Invoke(this, EventArgs.Empty);

}

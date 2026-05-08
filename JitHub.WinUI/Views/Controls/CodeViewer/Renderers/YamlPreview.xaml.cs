using JitHub.WinUI.ViewModels.CodeViewer;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.WinUI.Views.Controls.CodeViewer.Renderers;

/// <summary>
/// Renders YAML with syntax highlighting. DataContext must be a <see cref="RepoFilePreviewViewModel"/>.
/// </summary>
public sealed partial class YamlPreview : UserControl
{
    public YamlPreview()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private RepoFilePreviewViewModel? ViewModel => DataContext as RepoFilePreviewViewModel;

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        Editor.Text = ViewModel?.Text ?? string.Empty;
    }
}

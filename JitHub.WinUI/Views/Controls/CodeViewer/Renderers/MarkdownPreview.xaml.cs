using System;
using JitHub.WinUI.ViewModels.CodeViewer;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace JitHub.WinUI.Views.Controls.CodeViewer.Renderers;

/// <summary>
/// Renders Markdown files with a rich/plain toggle.
/// DataContext must be a <see cref="RepoFilePreviewViewModel"/>.
/// </summary>
public sealed partial class MarkdownPreview : UserControl
{
    public MarkdownPreview()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private RepoFilePreviewViewModel? ViewModel => DataContext as RepoFilePreviewViewModel;

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        SyncSegmented();
        SyncPanels();
        Bindings.Update();
    }

    private void SyncSegmented()
    {
        ViewModeSegmented.SelectedIndex = (ViewModel?.ShowRichPreview ?? true) ? 0 : 1;
    }

    private void SyncPanels()
    {
        bool rich = ViewModel?.ShowRichPreview ?? true;
        RichPanel.Visibility = rich ? Visibility.Visible : Visibility.Collapsed;
        PlainPanel.Visibility = rich ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ViewModeSegmented_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var vm = ViewModel;
        if (vm is null) return;
        bool wantsRich = ViewModeSegmented.SelectedIndex == 0;
        if (vm.ShowRichPreview != wantsRich)
            vm.ShowRichPreview = wantsRich;
        SyncPanels();
    }

    private void RichPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Cap images at the viewport width (minus padding), never exceeding the reading max-width.
        double maxW = Math.Max(0, Math.Min(e.NewSize.Width - 32, 860));
        ConstrainImagesInVisualTree(RichMarkdown, maxW);
    }

    private static void ConstrainImagesInVisualTree(DependencyObject parent, double maxWidth)
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Image img)
            {
                img.MaxWidth = maxWidth;
                // Clear any explicit Height set by the renderer so the image height
                // reflows based on the constrained width (no letterboxing).
                img.Height = double.NaN;
                img.Stretch = Stretch.Uniform;
            }
            ConstrainImagesInVisualTree(child, maxWidth);
        }
    }
}

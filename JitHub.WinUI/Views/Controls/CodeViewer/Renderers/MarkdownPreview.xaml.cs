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
        // Re-walk every time the MarkdownTextBlock finishes a layout pass.
        // Images are rendered asynchronously, so SizeChanged alone fires too early.
        RichMarkdown.LayoutUpdated += OnMarkdownLayoutUpdated;
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
        ApplyImageConstraints();
    }

    private void OnMarkdownLayoutUpdated(object? sender, object e)
    {
        // Called after every layout pass — catches images that load after SizeChanged.
        ApplyImageConstraints();
    }

    private void ApplyImageConstraints()
    {
        double maxW = Math.Max(0, Math.Min(RichPanel.ActualWidth - 32, 860));
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
                // Clear any explicit Height set by the renderer so height reflows
                // naturally from the constrained width (no top/bottom empty space).
                img.Height = double.NaN;
                img.MaxHeight = double.PositiveInfinity;
                img.Stretch = Stretch.Uniform;
            }
            else if (child is FrameworkElement fe && fe.ActualHeight > 0)
            {
                // Also clear explicit heights on direct wrappers around images.
                if (ContainsImage(child))
                    fe.Height = double.NaN;
            }
            ConstrainImagesInVisualTree(child, maxWidth);
        }
    }

    private static bool ContainsImage(DependencyObject parent)
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Image) return true;
            if (ContainsImage(child)) return true;
        }
        return false;
    }
}

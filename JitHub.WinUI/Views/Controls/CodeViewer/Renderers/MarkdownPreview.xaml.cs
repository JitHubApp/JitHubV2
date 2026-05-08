using System;
using JitHub.WinUI.ViewModels.CodeViewer;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

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
        InjectThemeDictionaries();
        DataContextChanged += OnDataContextChanged;
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

    // ── Theme resource injection ────────────────────────────────────────────────
    // CT MarkdownTextBlock resolves colors via {ThemeResource}, so overrides must
    // live in Resources.ThemeDictionaries["Light"] / ["Dark"] / ["Default"].
    // Writing to Resources[key] directly is a non-theme bag that ThemeResource ignores.

    private void InjectThemeDictionaries()
    {
        var light = new ResourceDictionary();
        light["InlineCodeBackground"]      = Brush("#E8E3D8");
        light["InlineCodeForeground"]      = Brush("#223127");
        light["CodeBlockBackground"]       = Brush("#EDE8DD");
        light["CodeBlockForeground"]       = Brush("#223127");
        light["HyperlinkButtonForeground"] = Brush("#3E7B64");

        var dark = new ResourceDictionary();
        dark["InlineCodeBackground"]      = Brush("#303830");
        dark["InlineCodeForeground"]      = Brush("#C7CDBF");
        dark["CodeBlockBackground"]       = Brush("#1C221C");
        dark["CodeBlockForeground"]       = Brush("#C7CDBF");
        dark["HyperlinkButtonForeground"] = Brush("#77B59A");

        // "Default" must be a separate instance — a ResourceDictionary can only
        // have one parent; sharing the same instance between two keys throws.
        var defaultDict = new ResourceDictionary();
        defaultDict["InlineCodeBackground"]      = Brush("#303830");
        defaultDict["InlineCodeForeground"]      = Brush("#C7CDBF");
        defaultDict["CodeBlockBackground"]       = Brush("#1C221C");
        defaultDict["CodeBlockForeground"]       = Brush("#C7CDBF");
        defaultDict["HyperlinkButtonForeground"] = Brush("#77B59A");

        Resources.ThemeDictionaries["Light"]   = light;
        Resources.ThemeDictionaries["Dark"]    = dark;
        Resources.ThemeDictionaries["Default"] = defaultDict;
    }

    private static SolidColorBrush Brush(string hex)
    {
        hex = hex.TrimStart('#');
        return new SolidColorBrush(Color.FromArgb(0xFF,
            Convert.ToByte(hex[0..2], 16),
            Convert.ToByte(hex[2..4], 16),
            Convert.ToByte(hex[4..6], 16)));
    }

    // ── Image constraint ───────────────────────────────────────────────────────

    private void RichPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyImageConstraints();
    }

    private void OnMarkdownLayoutUpdated(object? sender, object e)
    {
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
                img.Height = double.NaN;
                img.MaxHeight = double.PositiveInfinity;
                img.Stretch = Stretch.Uniform;
            }
            else if (child is FrameworkElement fe && ContainsImage(child))
            {
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

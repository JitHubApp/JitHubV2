using System;
using JitHub.WinUI.ViewModels.CodeViewer;
using Microsoft.UI;
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
        DataContextChanged += OnDataContextChanged;
        RichMarkdown.LayoutUpdated += OnMarkdownLayoutUpdated;
        ActualThemeChanged += (_, _) => UpdateThemeColors();
        Loaded += (_, _) => UpdateThemeColors();
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

    // ── Imperative theme resource injection ────────────────────────────────────
    // ThemeDictionaries are unreliable for CT MarkdownTextBlock because the
    // control may resolve resources from its own scope. We set values directly
    // in UserControl.Resources so they sit exactly at the right lookup scope,
    // and we refresh them whenever the actual theme changes.

    private void UpdateThemeColors()
    {
        bool dark = ActualTheme == ElementTheme.Dark;
        SetBrush("InlineCodeBackground",     dark ? "#303830" : "#E8E3D8");
        SetBrush("InlineCodeForeground",     dark ? "#C7CDBF" : "#223127");
        SetBrush("CodeBlockBackground",      dark ? "#1C221C" : "#EDE8DD");
        SetBrush("CodeBlockForeground",      dark ? "#C7CDBF" : "#223127");
        SetBrush("HyperlinkButtonForeground", dark ? "#77B59A" : "#3E7B64");
    }

    private void SetBrush(string key, string hex)
    {
        Resources[key] = new SolidColorBrush(ParseHex(hex));
    }

    private static Color ParseHex(string hex)
    {
        hex = hex.TrimStart('#');
        return Color.FromArgb(0xFF,
            Convert.ToByte(hex[0..2], 16),
            Convert.ToByte(hex[2..4], 16),
            Convert.ToByte(hex[4..6], 16));
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

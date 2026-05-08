using System;
using CommunityToolkit.WinUI.Controls;
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
    // Persistent brush instances shared with MarkdownThemes.
    // On theme change we only mutate .Color — the already-rendered elements
    // update in-place without a full Markdown re-render.
    private readonly SolidColorBrush _inkBrush     = new();
    private readonly SolidColorBrush _subtleBrush  = new();
    private readonly SolidColorBrush _accentBrush  = new();
    private readonly SolidColorBrush _borderBrush  = new();
    private readonly SolidColorBrush _inlineCodeBg = new();
    private readonly SolidColorBrush _codeBlockBg  = new();
    private readonly SolidColorBrush _tableHeadBg  = new();
    private readonly MarkdownConfig  _markdownConfig;

    public MarkdownPreview()
    {
        InitializeComponent();

        UpdateBrushColors(ActualTheme == ElementTheme.Dark);

        _markdownConfig = new MarkdownConfig
        {
            Themes = new MarkdownThemes
            {
                H1Foreground = _inkBrush,
                H2Foreground = _inkBrush,
                H3Foreground = _inkBrush,
                H4Foreground = _inkBrush,
                H5Foreground = _inkBrush,
                H6Foreground = _inkBrush,
                InlineCodeBackground  = _inlineCodeBg,
                InlineCodeForeground  = _inkBrush,
                InlineCodeBorderBrush = _borderBrush,
                CodeBlockBackground   = _codeBlockBg,
                CodeBlockForeground   = _inkBrush,
                CodeBlockBorderBrush  = _borderBrush,
                LinkForeground         = _accentBrush,
                QuoteForeground        = _subtleBrush,
                QuoteBorderBrush       = _accentBrush,
                BorderBrush            = _borderBrush,
                TableBorderBrush       = _borderBrush,
                TableHeadingBackground = _tableHeadBg,
                HorizontalRuleBrush    = _borderBrush,
                // ImageMaxWidth = 0 means no theme-level cap; the LayoutUpdated
                // walker applies a live, container-responsive MaxWidth instead.
                ImageMaxWidth  = 0,
                ImageMaxHeight = 0,
                ImageStretch   = Stretch.Uniform,
            },
        };

        RichMarkdown.Config = _markdownConfig;

        DataContextChanged             += OnDataContextChanged;
        ActualThemeChanged             += (_, _) => UpdateBrushColors(ActualTheme == ElementTheme.Dark);
        RichMarkdown.LayoutUpdated     += OnMarkdownLayoutUpdated;
    }

    private RepoFilePreviewViewModel? ViewModel => DataContext as RepoFilePreviewViewModel;

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        SyncSegmented();
        SyncPanels();
        RichMarkdown.Text = ViewModel?.Text ?? string.Empty;
        Bindings.Update();
    }

    private void SyncSegmented()
    {
        ViewModeSegmented.SelectedIndex = (ViewModel?.ShowRichPreview ?? true) ? 0 : 1;
    }

    private void SyncPanels()
    {
        bool rich = ViewModel?.ShowRichPreview ?? true;
        RichPanel.Visibility  = rich ? Visibility.Visible : Visibility.Collapsed;
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

    // ── Color theming — mutable brushes ──────────────────────────────────────

    private void UpdateBrushColors(bool dark)
    {
        _inkBrush.Color     = ParseColor(dark ? "#F0F2EA" : "#223127");
        _subtleBrush.Color  = ParseColor(dark ? "#99A294" : "#6D7A70");
        _accentBrush.Color  = ParseColor(dark ? "#77B59A" : "#3E7B64");
        _borderBrush.Color  = ParseColor(dark ? "#3C463E" : "#D5CBB7");
        _inlineCodeBg.Color = ParseColor(dark ? "#303830" : "#E8E3D8");
        _codeBlockBg.Color  = ParseColor(dark ? "#1C221C" : "#EDE8DD");
        _tableHeadBg.Color  = ParseColor(dark ? "#252B25" : "#F7F0E1");
    }

    private static Color ParseColor(string hex)
    {
        hex = hex.TrimStart('#');
        return Color.FromArgb(0xFF,
            Convert.ToByte(hex[0..2], 16),
            Convert.ToByte(hex[2..4], 16),
            Convert.ToByte(hex[4..6], 16));
    }

    // ── Image width constraint ────────────────────────────────────────────────
    // MarkdownThemes.ImageMaxWidth is a fixed cap and cannot respond to the
    // live container width. We walk the visual tree on LayoutUpdated (which fires
    // after async image loads complete) to clamp MaxWidth to the container width
    // and clear any fixed Height the renderer may have set (fixed Height causes
    // letterboxing with Stretch.Uniform).

    private void OnMarkdownLayoutUpdated(object? sender, object e)
    {
        double maxW = Math.Max(0, Math.Min(RichPanel.ActualWidth - 32, 860));
        ConstrainImages(RichMarkdown, maxW);
    }

    private static void ConstrainImages(DependencyObject parent, double maxW)
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Image img)
            {
                // Clamp to container width. Keep the smaller of the two so we
                // never upscale beyond the image's natural size.
                img.MaxWidth = (img.MaxWidth > 0 && img.MaxWidth < maxW) ? img.MaxWidth : maxW;
                img.Height   = double.NaN;  // clear any fixed height to prevent letterboxing
            }
            else if (child is FrameworkElement fe && ContainsImage(child))
            {
                fe.Height = double.NaN;     // clear fixed height on image wrapper elements too
            }
            ConstrainImages(child, maxW);
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

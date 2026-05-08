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
    public MarkdownPreview()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        ActualThemeChanged += (_, _) => ApplyMarkdown();
    }

    private RepoFilePreviewViewModel? ViewModel => DataContext as RepoFilePreviewViewModel;

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        SyncSegmented();
        SyncPanels();
        ApplyMarkdown();
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

    // ── Markdown config & color theming ──────────────────────────────────────
    // CT MarkdownTextBlock reads colors from MarkdownConfig.Themes (a C# object
    // with direct Brush properties — not XAML resource keys).  We build a fresh
    // MarkdownConfig for the current theme and set it BEFORE assigning Text so
    // the very first render already uses the correct colors.

    private void ApplyMarkdown()
    {
        RichMarkdown.Config = BuildConfig();

        // Force a full re-render by clearing Text first (setting null! skips the
        // render path in OnTextChanged which requires NewValue != null; then
        // restoring the real text triggers a full re-render with the new config).
        RichMarkdown.Text = null!;
        RichMarkdown.Text = ViewModel?.Text ?? string.Empty;
    }

    private MarkdownConfig BuildConfig()
    {
        bool dark = ActualTheme == ElementTheme.Dark;

        var ink    = dark ? "#F0F2EA" : "#223127";
        var accent = dark ? "#77B59A" : "#3E7B64";
        var subtle = dark ? "#99A294" : "#6D7A70";
        var border = dark ? "#3C463E" : "#D5CBB7";
        var inlineBg    = dark ? "#303830" : "#E8E3D8";
        var codeBlockBg = dark ? "#1C221C" : "#EDE8DD";
        var tableHead   = dark ? "#252B25" : "#F7F0E1";

        return new MarkdownConfig
        {
            Themes = new MarkdownThemes
            {
                // Headers — use app ink color so they're always legible
                H1Foreground = Brush(ink),
                H2Foreground = Brush(ink),
                H3Foreground = Brush(ink),
                H4Foreground = Brush(ink),
                H5Foreground = Brush(ink),
                H6Foreground = Brush(ink),

                // Inline code
                InlineCodeBackground = Brush(inlineBg),
                InlineCodeForeground = Brush(ink),

                // Fenced code block
                CodeBlockBackground = Brush(codeBlockBg),
                CodeBlockForeground = Brush(ink),

                // Links
                LinkForeground = Brush(accent),

                // Quotes, tables, rules
                QuoteForeground       = Brush(subtle),
                QuoteBorderBrush      = Brush(accent),
                BorderBrush           = Brush(border),
                TableBorderBrush      = Brush(border),
                TableHeadingBackground = Brush(tableHead),
                HorizontalRuleBrush   = Brush(border),

                // Images — let MarkdownThemes handle sizing natively;
                // no visual-tree walk needed.
                ImageMaxWidth  = 860,
                ImageMaxHeight = 0,   // 0 = no height cap; height flows naturally
                ImageStretch   = Stretch.Uniform,
            },
        };
    }

    private static SolidColorBrush Brush(string hex)
    {
        hex = hex.TrimStart('#');
        return new SolidColorBrush(Color.FromArgb(0xFF,
            Convert.ToByte(hex[0..2], 16),
            Convert.ToByte(hex[2..4], 16),
            Convert.ToByte(hex[4..6], 16)));
    }
}

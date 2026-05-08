using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Gfm;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Sample;

public sealed partial class MainWindow : Window
{
    private readonly MarkdownRendererControl _renderer;
    private readonly TextBox _editor;

    public MainWindow()
    {
        Title = "MarkdownRenderer sample";
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _editor = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
            FontSize = 13,
            Margin = new Thickness(8),
            Text = SampleMarkdown,
        };
        _editor.TextChanged += (_, _) => { if (_renderer is not null) _renderer.Markdown = _editor.Text ?? string.Empty; };

        var registry = new MarkdownExtensionRegistry()
            .UseGitHubFlavoredMarkdown();
        _renderer = new MarkdownRendererControl
        {
            Markdown = SampleMarkdown,
            ExtensionRegistry = registry,
            Theme = new MarkdownTheme(),
            Margin = new Thickness(8),
        };
        _renderer.LinkClick += (_, e) =>
        {
            try { _ = Windows.System.Launcher.LaunchUriAsync(new Uri(e.Url)); } catch { }
        };

        Grid.SetColumn(_editor, 0);
        Grid.SetColumn(_renderer, 1);
        grid.Children.Add(_editor);
        grid.Children.Add(_renderer);

        this.Content = grid;
    }

    private const string SampleMarkdown = """
        # MarkdownRenderer

        A **native** Win2D + DirectWrite markdown renderer.

        ## Features

        - Off-thread parsing with [Markdig](https://github.com/xoofx/markdig)
        - Custom flow layout engine
        - DOM-style selection and `Ctrl+C` copies the *exact* original markdown

        > Theming follows Win11 design tokens and switches with the system
        > automatically.

        ```csharp
        var control = new MarkdownRendererControl { Markdown = "# hi" };
        ```

        ---

        Try selecting across these elements and pressing **Ctrl+C**.
        """;
}

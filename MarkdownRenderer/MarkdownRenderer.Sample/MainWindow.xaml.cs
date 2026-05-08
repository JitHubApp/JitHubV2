using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Gfm;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Sample;

public sealed partial class MainWindow : Window
{
    private readonly MarkdownRendererControl _renderer;
    private readonly TextBox _editor;

    // Mapping from tab label → sample markdown for each feature
    private static readonly Dictionary<string, string> Samples = new()
    {
        ["Typography"] = TypographySample,
        ["Lists"]      = ListsSample,
        ["Tables"]     = TablesSample,
        ["Code"]       = CodeSample,
        ["GFM Alerts"] = AlertsSample,
        ["Images"]     = ImagesSample,
        ["Embeds"]     = EmbedsSample,
        ["Selection"]  = SelectionSample,
        ["Full Demo"]  = FullDemoSample,
    };

    public MainWindow()
    {
        Title = "MarkdownRenderer — Feature Showcase";

        var rootGrid = new Grid();
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // toolbar
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // content

        // ── toolbar ────────────────────────────────────────────────────────────
        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Padding = new Thickness(8, 6, 8, 4),
        };

        foreach (var (label, _) in Samples)
        {
            var btn = new Button
            {
                Content = label,
                Tag = label,
                Padding = new Thickness(10, 4, 10, 4),
            };
            btn.Click += OnSampleButtonClick;
            toolbar.Children.Add(btn);
        }

        var themeToggle = new ToggleButton
        {
            Content = "☀ Light / 🌙 Dark",
            Margin = new Thickness(16, 0, 0, 0),
            Padding = new Thickness(10, 4, 10, 4),
        };
        themeToggle.Checked   += (_, _) => SetTheme(ElementTheme.Dark);
        themeToggle.Unchecked += (_, _) => SetTheme(ElementTheme.Light);
        toolbar.Children.Add(themeToggle);

        Grid.SetRow(toolbar, 0);
        rootGrid.Children.Add(toolbar);

        // ── editor + renderer split ─────────────────────────────────────────────
        var contentGrid = new Grid();
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4, GridUnitType.Pixel) }); // splitter
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _editor = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 13,
            Padding = new Thickness(8),
            Text = FullDemoSample,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_editor, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(_editor, ScrollBarVisibility.Auto);
        _editor.TextChanged += (_, _) =>
        {
            if (_renderer is not null) _renderer.Markdown = _editor.Text ?? string.Empty;
        };

        var splitter = new Border
        {
            Background = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
        };

        var registry = new MarkdownExtensionRegistry().UseGitHubFlavoredMarkdown();

        _renderer = new MarkdownRendererControl
        {
            Markdown = FullDemoSample,
            ExtensionRegistry = registry,
            Theme = new MarkdownTheme(),
            EmbedFactory = new SampleEmbedFactory(),
            Margin = new Thickness(0),
        };
        _renderer.LinkClick += (_, e) =>
        {
            try { _ = Windows.System.Launcher.LaunchUriAsync(new Uri(e.Url)); } catch { }
        };

        var rendererScroll = new ScrollViewer
        {
            Content = _renderer,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(12, 8, 12, 8),
        };

        Grid.SetColumn(_editor, 0);
        Grid.SetColumn(splitter, 1);
        Grid.SetColumn(rendererScroll, 2);
        contentGrid.Children.Add(_editor);
        contentGrid.Children.Add(splitter);
        contentGrid.Children.Add(rendererScroll);

        Grid.SetRow(contentGrid, 1);
        rootGrid.Children.Add(contentGrid);

        Content = rootGrid;
    }

    private void OnSampleButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string label && Samples.TryGetValue(label, out var md))
        {
            _editor.Text = md;
        }
    }

    private void SetTheme(ElementTheme theme)
    {
        if (Content is FrameworkElement fe) fe.RequestedTheme = theme;
    }

    // ── Sample markdown strings ────────────────────────────────────────────────

    private const string TypographySample = """
        # Heading 1 — The quick brown fox
        ## Heading 2 — The quick brown fox
        ### Heading 3 — The quick brown fox
        #### Heading 4 — The quick brown fox
        ##### Heading 5 — The quick brown fox
        ###### Heading 6 — The quick brown fox

        ---

        Regular paragraph text. Lorem ipsum dolor sit amet, consectetur adipiscing elit.
        Sed do eiusmod tempor incididunt ut labore et dolore magna aliqua.

        **Bold text** is rendered with semibold weight.
        *Italic text* is rendered with italic style.
        ***Bold and italic*** combined.
        ~~Strikethrough~~ for deleted content.
        Inline `code span` uses a monospace font with a subtle background.

        A [hyperlink](https://github.com/JitHubApp/JitHubV2) with underline decoration.
        An autolink: <https://microsoft.com>

        > A blockquote with an accent bar on the left.
        > It can span multiple lines and contain **inline formatting**.
        >
        > Multiple paragraphs in a blockquote are supported.

        ---

        Thematic breaks (`---`) divide sections.
        """;

    private const string ListsSample = """
        ## Unordered Lists

        - First item
        - Second item with **bold** text
        - Third item with `inline code`
          - Nested level 2
          - Another nested item
            - Nested level 3
        - Back to top level

        ## Ordered Lists

        1. First step
        2. Second step with [a link](https://example.com)
        3. Third step
           1. Sub-step A
           2. Sub-step B
        4. Fourth step

        ## Task Lists (GFM)

        - [x] Design the layout engine
        - [x] Implement Win2D painter
        - [x] Threading safety with ThemeSnapshot
        - [ ] Full ITextProvider accessibility
        - [ ] Performance profiling on real documents

        ## Mixed content in list items

        - Item with a paragraph
          
          Followed by a second paragraph inside the same list item.

        - Another item

        1. An ordered item containing a code block:

           ```csharp
           int sum = Enumerable.Range(1, 10).Sum();
           ```

        2. And back to normal.
        """;

    private const string TablesSample = """
        ## GFM Tables

        | Feature | Status | Notes |
        |---------|--------|-------|
        | Headings H1–H6 | ✅ Done | Win11 typography tokens |
        | Bold / Italic | ✅ Done | Per-run style spans |
        | Inline code | ✅ Done | Monospace + bg fill |
        | Code blocks | ✅ Done | Syntax-agnostic |
        | Blockquotes | ✅ Done | Accent bar |
        | Unordered lists | ✅ Done | 2-column ListItemBox |
        | Ordered lists | ✅ Done | Numeric marker |
        | Task lists | ✅ Done | ☑/☐ via GFM ext |
        | Tables | ✅ Done | Equal-width columns |
        | GFM Alerts | ✅ Done | NOTE/TIP/WARNING/CAUTION |
        | Footnotes | ✅ Done | Unicode superscripts |
        | Thematic breaks | ✅ Done | Styled separator |
        | Links | ✅ Done | Click-to-launch |
        | Selection + Copy | ✅ Done | Source-accurate markdown |
        | AOT compatibility | ✅ Done | No reflection dispatch |
        | Live theme switch | ✅ Done | No reload needed |

        ## Smaller Table

        | Name | Type | Default |
        |------|------|---------|
        | `Markdown` | `string` | `""` |
        | `Theme` | `MarkdownTheme` | Win11 defaults |
        | `ExtensionRegistry` | `MarkdownExtensionRegistry` | CommonMark only |
        | `FlowDirection` | `FlowDirection` | `LeftToRight` |
        """;

    private const string CodeSample = """
        ## Fenced Code Blocks

        C# example:

        ```csharp
        public sealed class MarkdownRendererControl : UserControl
        {
            private volatile LayoutSnapshot? _snapshot;

            public string Markdown
            {
                get => (string)GetValue(MarkdownProperty);
                set => SetValue(MarkdownProperty, value);
            }

            private async Task RebuildAsync(CancellationToken ct)
            {
                var device = CanvasDevice.GetSharedDevice();
                var ctx = new MarkdownLayoutContext(device, themeSnapshot, sourceMap, registry, FlowDirection);
                _snapshot = await Task.Run(() => builder.Build(doc, width), ct);
                _canvas.Invalidate();
            }
        }
        ```

        Python example:

        ```python
        import asyncio

        async def render_markdown(text: str) -> LayoutSnapshot:
            pipeline = build_pipeline()
            document = await asyncio.to_thread(pipeline.parse, text)
            return await asyncio.to_thread(layout_builder.build, document)
        ```

        Indented code block (4 spaces):

            var x = 42;
            Console.WriteLine($"The answer is {x}");

        Inline `code` uses a background highlight.

        ## Language tags

        The renderer stores the `FencedCodeBlock.Info` property (e.g. `"csharp"`)
        on the source map entry — a future syntax-highlighting pass can use this
        to apply per-token colors via `CanvasTextLayout.SetColor`.
        """;

    private const string AlertsSample = """
        ## GitHub Flavored Markdown Alerts

        > [!NOTE]
        > Useful information that users should know, even when skimming content.

        > [!TIP]
        > Helpful advice for doing things better or more easily.

        > [!IMPORTANT]
        > Key information users need to know to achieve their goal.

        > [!WARNING]
        > Urgent info that needs immediate user attention to avoid problems.

        > [!CAUTION]
        > Advises about risks or negative outcomes of certain actions.

        ---

        A regular blockquote (not an alert) still works:

        > "The best performance optimization is eliminating unnecessary work."
        > — unknown

        ## Footnotes (GFM)

        Footnotes let you add references without cluttering the main text.[^1]
        They render as Unicode superscripts[^2] in the text body and collect
        as a definition list at the bottom of the document.

        [^1]: This is the first footnote definition.
        [^2]: Superscript characters: ¹²³⁴⁵⁶⁷⁸⁹
        """;

    private const string SelectionSample = """
        ## DOM-Style Text Selection

        Click and drag to select any text in this rendered document.
        The selection spans across different element types seamlessly.

        ### Try selecting across these:

        - A list item with **bold** and *italic* text
        - Another item with `inline code` inside it
        - A third item with a [link to GitHub](https://github.com)

        > A blockquote with some text inside it.

        Once you have a selection, press **Ctrl+C** to copy.

        The copied text will be the **exact original Markdown source** for the
        selected region — not the rendered text, not HTML, but the raw `.md` syntax
        that produced those rendered elements.

        ### How it works

        Each rendered character position maps back to a source offset via
        `MarkdownSourceMap`. When you copy:

        1. The `DocumentRange` (start block, inline, char offset → end block, inline, char offset) is resolved
        2. Each intersecting source span is extracted from the original markdown string
        3. The spans are joined and placed on the clipboard as plain text

        Press **Ctrl+A** to select the entire document.
        """;

    private const string FullDemoSample = """
        # MarkdownRenderer

        A **fully native** Win2D + DirectWrite markdown renderer for WinUI 3.
        Built with [Markdig](https://github.com/xoofx/markdig) for parsing and a
        custom flow layout engine for pixel-perfect rendering.

        ## Core Features

        - **Off-thread parsing** — parse + layout runs on a background thread; the
          UI thread only schedules a repaint when the snapshot is ready
        - **Win11-native typography** — Segoe UI Variable, type scale, and color
          tokens from the Windows design system
        - **Live theme switching** — light ↔ dark without reloading or re-parsing
        - **DOM-style selection** — drag to select across any element types and press
          `Ctrl+C` to copy the *exact original markdown source*
        - **GFM extensions** — tables, task lists, alerts, footnotes via the
          `MarkdownRenderer.Gfm` package
        - **AOT compatible** — no reflection, all dispatch through virtual calls

        ---

        ## Inline Formatting

        **Bold**, *italic*, ***bold italic***, ~~strikethrough~~, `inline code`.
        A [hyperlink](https://github.com) with click-to-launch.

        ## Blockquote

        > Theming follows Win11 design tokens and switches with the system
        > automatically — no page reload, no flicker.

        ## GFM Alerts

        > [!NOTE]
        > Resources are always created with `CanvasDevice.GetSharedDevice()` so
        > layout can run safely off the UI thread.

        > [!TIP]
        > Register custom renderers via `MarkdownExtensionRegistry` to handle any
        > Markdig AST node type with your own `BlockBox` implementation.

        > [!WARNING]
        > `MarkdownTheme` is a `DependencyObject` — only read its properties on
        > the UI thread. Use `ThemeResolver.CreateSnapshot()` before dispatching
        > to a background task.

        ## Task List

        - [x] Project scaffolding + slnx integration
        - [x] Markdig parser + extension registry
        - [x] Flow layout engine (StackBox, InlineContainerBox, TableBox)
        - [x] Win2D CanvasVirtualControl painter
        - [x] ThemeSnapshot threading safety
        - [x] AOT-safe renderer dispatch
        - [x] GFM: tables, task lists, alerts, footnotes
        - [x] ListItemBox — bullet and text side by side
        - [ ] Full ITextProvider accessibility peer
        - [ ] Per-language syntax highlighting

        ## Table

        | Layer | Technology | Purpose |
        |-------|-----------|---------|
        | Parsing | Markdig | CommonMark + GFM AST |
        | Layout | Custom C# | Flow layout, box model |
        | Rendering | Win2D / DirectWrite | GPU-accelerated text |
        | Theming | WinUI 3 resources | Win11 design tokens |
        | Selection | MarkdownSourceMap | Source-accurate copy |

        ## Code

        ```csharp
        // Register GFM extensions and create the control
        var registry = new MarkdownExtensionRegistry()
            .UseGitHubFlavoredMarkdown();

        var control = new MarkdownRendererControl
        {
            Markdown = markdownSource,
            ExtensionRegistry = registry,
            Theme = new MarkdownTheme(),
        };
        ```

        ---

        *Select any text above and press **Ctrl+C** to copy the exact source markdown.*
        """;

    private const string ImagesSample = """
        # Images

        Inline images load asynchronously via Win2D `CanvasBitmap.LoadAsync`. Each
        is decoded on the GPU and re-laid-out once dimensions are known.

        ## GitHub avatar

        ![octocat](https://avatars.githubusercontent.com/u/583231?v=4)

        ## A wider banner

        ![banner](https://github.githubassets.com/images/modules/site/home/hero-glow.svg)

        ## Broken image (graceful failure)

        ![missing](https://example.invalid/does-not-exist.png)

        Captions can wrap around the layout normally.
        """;

    private const string EmbedsSample = """
        # Hosted WinUI Embeds

        The renderer hosts native WinUI controls via `IMarkdownEmbedFactory`.
        This sample registers a factory that intercepts fenced code blocks of
        the form ```` ```button:Label ```` and replaces them with a real
        `Microsoft.UI.Xaml.Controls.Button`.

        Try it:

        ```button:Click me
        ```

        ```button:Another action
        ```

        Anything else (paragraphs, tables, lists) renders normally — the
        factory only opts in for blocks it recognizes.

        ## Task lists also use real CheckBox controls

        - [x] Markdig integration
        - [x] Flow layout
        - [ ] You can't toggle these because they're disabled — but they're
              still real WinUI CheckBoxes hosted on the overlay
        """;
}

/// <summary>
/// Demonstrates <see cref="IMarkdownEmbedFactory"/> by intercepting fenced
/// code blocks whose info-string starts with <c>button:</c> and rendering them
/// as native WinUI <see cref="Button"/>s.
/// </summary>
internal sealed class SampleEmbedFactory : MarkdownRenderer.Hosting.IMarkdownEmbedFactory
{
    public bool CanCreate(Markdig.Syntax.Block block)
    {
        return block is Markdig.Syntax.FencedCodeBlock fc
            && (fc.Info?.StartsWith("button:", StringComparison.Ordinal) ?? false);
    }

    public float MeasureHeight(Markdig.Syntax.Block block, float availableWidth)
    {
        // Simple Button at default WinUI metrics is ~32px tall.
        return 36f;
    }

    public Microsoft.UI.Xaml.FrameworkElement CreateBlock(Markdig.Syntax.Block block)
    {
        var fc = (Markdig.Syntax.FencedCodeBlock)block;
        string label = fc.Info!.Substring("button:".Length);
        var btn = new Button
        {
            Content = label,
            Padding = new Thickness(12, 4, 12, 4),
        };
        btn.Click += (_, _) =>
        {
            var dlg = new ContentDialog
            {
                Title = "Embed clicked",
                Content = $"Hello from “{label}”! This is a real WinUI Button hosted by the markdown renderer.",
                CloseButtonText = "OK",
                XamlRoot = btn.XamlRoot,
            };
            _ = dlg.ShowAsync();
        };
        return btn;
    }
}


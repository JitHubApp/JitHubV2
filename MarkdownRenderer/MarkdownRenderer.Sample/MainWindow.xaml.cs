using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
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
    private TextBlock? _realizedCountStatus;
    private TextBlock? _flowDirectionStatus;

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
        ["RTL"]        = RtlSample,
        ["Virtualization"] = "", // generated lazily in OnSampleButtonClick
        ["Selection"]  = SelectionSample,
        ["Lazy Images"]   = LazyImagesSample,
        ["Scroll Anchor"] = ScrollAnchorSample,
        ["Footnotes"]     = FootnotesSample,
        ["Keyboard Nav"]  = KeyboardNavSample,
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
            AutomationProperties.SetAutomationId(btn, "SampleButton_" + label.Replace(' ', '_'));
            AutomationProperties.SetName(btn, label);
            btn.Click += OnSampleButtonClick;
            toolbar.Children.Add(btn);
        }

        var themeToggle = new ToggleButton
        {
            Content = "☀ Light / 🌙 Dark",
            Margin = new Thickness(16, 0, 0, 0),
            Padding = new Thickness(10, 4, 10, 4),
        };
        AutomationProperties.SetAutomationId(themeToggle, "ThemeToggle");
        AutomationProperties.SetName(themeToggle, "Theme");
        themeToggle.Checked   += (_, _) => SetTheme(ElementTheme.Dark);
        themeToggle.Unchecked += (_, _) => SetTheme(ElementTheme.Light);
        toolbar.Children.Add(themeToggle);

        var rtlToggle = new ToggleButton
        {
            Content = "↔ RTL",
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(10, 4, 10, 4),
            Name = "RtlToggle",
        };
        AutomationProperties.SetAutomationId(rtlToggle, "RtlToggle");
        AutomationProperties.SetName(rtlToggle, "RTL");
        rtlToggle.Checked   += (_, _) => { if (_renderer is not null) { _renderer.FlowDirection = FlowDirection.RightToLeft; UpdateFlowDirectionStatus(); } };
        rtlToggle.Unchecked += (_, _) => { if (_renderer is not null) { _renderer.FlowDirection = FlowDirection.LeftToRight; UpdateFlowDirectionStatus(); } };
        toolbar.Children.Add(rtlToggle);

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
        AutomationProperties.SetAutomationId(_renderer, "MarkdownRenderer");
        AutomationProperties.SetName(_renderer, "Markdown Renderer");
        _renderer.LinkClick += (_, e) =>
        {
            try { _ = Windows.System.Launcher.LaunchUriAsync(new Uri(e.Url)); } catch { }
        };

        // Hidden status TextBlock that mirrors RealizedEmbedCount so UI
        // automation tests can verify virtualisation without polluting the
        // renderer's own UIA surface (HelpText is read aloud by Narrator).
        _realizedCountStatus = new TextBlock
        {
            Text = "realized:0",
            Opacity = 0,
            IsHitTestVisible = false,
            Width = 1,
            Height = 1,
        };
        AutomationProperties.SetAutomationId(_realizedCountStatus, "RealizedEmbedCount");
        AutomationProperties.SetName(_realizedCountStatus, "realized:0");
        _renderer.EmbedsRealizationChanged += (_, _) =>
        {
            int n = _renderer.RealizedEmbedCount;
            string s = "realized:" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _realizedCountStatus.Text = s;
            AutomationProperties.SetName(_realizedCountStatus, s);
        };

        _flowDirectionStatus = new TextBlock
        {
            Text = "flow:ltr",
            Opacity = 0,
            IsHitTestVisible = false,
            Width = 1,
            Height = 1,
        };
        AutomationProperties.SetAutomationId(_flowDirectionStatus, "FlowDirectionStatus");
        AutomationProperties.SetName(_flowDirectionStatus, "flow:ltr");

        Grid.SetColumn(_editor, 0);
        Grid.SetColumn(splitter, 1);
        Grid.SetColumn(_renderer, 2);
        _renderer.Padding = new Thickness(12, 8, 12, 8);
        contentGrid.Children.Add(_editor);
        contentGrid.Children.Add(splitter);
        contentGrid.Children.Add(_renderer);

        Grid.SetRow(contentGrid, 1);
        rootGrid.Children.Add(contentGrid);
        rootGrid.Children.Add(_realizedCountStatus);
        rootGrid.Children.Add(_flowDirectionStatus);

        Content = rootGrid;
    }

    private void UpdateFlowDirectionStatus()
    {
        if (_flowDirectionStatus is null || _renderer is null) return;
        string s = _renderer.FlowDirection == FlowDirection.RightToLeft ? "flow:rtl" : "flow:ltr";
        _flowDirectionStatus.Text = s;
        AutomationProperties.SetName(_flowDirectionStatus, s);
    }

    private void OnSampleButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string label)
        {
            // VirtualizationSample is a static readonly built via runtime
            // concatenation, so it can't live in a const-string dict.
            string? md = label switch
            {
                "Virtualization" => VirtualizationSample,
                _ => Samples.TryGetValue(label, out var v) ? v : null,
            };
            if (md is not null) _editor.Text = md;
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
        # Images, captions, and SVG

        Inline images load asynchronously via Win2D `CanvasBitmap.LoadAsync`.
        Each is decoded on the GPU and re-laid-out once dimensions are known.
        **Alt-text becomes a caption** rendered under the image so screen
        readers and sighted readers see the same description.

        ## GitHub avatar (PNG, with caption)

        ![The GitHub Octocat — square avatar PNG, 460×460](https://avatars.githubusercontent.com/u/583231?v=4)

        ## Inline SVG (data: URI)

        ![A blue 64×64 circle drawn entirely in SVG](data:image/svg+xml;utf8,<svg xmlns='http://www.w3.org/2000/svg' width='64' height='64' viewBox='0 0 64 64'><circle cx='32' cy='32' r='28' fill='%230078D4'/></svg>)

        ## Remote SVG (HTTP)

        ![GitHub site hero glow — a wide SVG decoration from github.com](https://github.githubassets.com/images/modules/site/home/hero-glow.svg)

        ## Broken image (graceful failure)

        ![A 1×1 placeholder showing a friendly error icon](https://example.invalid/does-not-exist.png)

        Captions wrap normally and respect the document's flow direction.
        """;

    private const string RtlSample = """
        # دعم الكتابة من اليمين إلى اليسار

        This page demonstrates **right-to-left (RTL)** layout. Toggle the **↔ RTL**
        button in the toolbar to flip the document's flow direction. Notice how:

        - The accent bar of blockquotes flips to the right edge.
        - List bullets and numbers move to the right side of the line.
        - Table columns reverse order so the first column reads first.
        - Inline mixed-bidi text shapes correctly (Arabic + English).

        > هذا اقتباس باللغة العربية. The quote bar should appear on the right
        > side when RTL is active, and on the left otherwise.

        ## قائمة (List)

        1. العنصر الأول — مع نص إنجليزي *italic* and **bold**
        2. العنصر الثاني — with `inline code`
        3. العنصر الثالث — and a [hyperlink to github.com](https://github.com)

        ## جدول (Table)

        | الاسم  | العمر | المدينة |
        | ------ | ----- | ------- |
        | أحمد   | 32    | القاهرة |
        | فاطمة  | 28    | دبي     |
        | سارة   | 41    | الرياض  |
        """;

    private static readonly string VirtualizationSample = """
        # Embed virtualization

        This sample renders a long document with many hosted WinUI button
        embeds (300 below). Only the embeds in the visible viewport are
        instantiated; off-screen ones are torn down and recreated when they
        scroll back into view. Watch the memory usage stay flat while
        scrolling — the renderer caps realised embeds to a bounded set.

        Tip: the realisation band extends ±400 px around the viewport, and
        the de-realisation band ±1200 px, providing hysteresis so embeds
        near the edge don't thrash.

        """ + GenerateVirtualizationButtons(300);

    private static string GenerateVirtualizationButtons(int count)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 1; i <= count; i++)
        {
            sb.Append("```button:Button #").Append(i).Append("\n```\n\n");
            if (i % 25 == 0) sb.Append("## Section ").Append(i / 25).Append("\n\n");
        }
        return sb.ToString();
    }

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

    // ── New feature sample pages ───────────────────────────────────────────────

    private static readonly string LazyImagesSample = """
        # Lazy Image Loading

        Images are only fetched when they are within **800 px** of the current
        viewport (the "overscan band"). Images far off-screen are not loaded
        until the user scrolls close to them.

        **How to observe this:**
        1. Open DevTools / Fiddler and watch HTTP traffic.
        2. When you first load this page, only the top few images will fire
           requests. Scroll down to trigger additional loads.
        3. Images already in the cache (`BitmapCache`) always appear instantly —
           they bypass the lazy-load gate entirely.

        ---

        ## Images below fold

        Scroll down to see each image load as you approach it.

        ![GitHub Octocat](https://github.githubassets.com/images/icons/emoji/octocat.png)

        Lorem ipsum dolor sit amet, consectetur adipiscing elit. Sed do eiusmod
        tempor incididunt ut labore et dolore magna aliqua. Ut enim ad minim
        veniam, quis nostrud exercitation ullamco laboris.

        ![GitHub Logo](https://github.githubassets.com/assets/GitHub-Mark-ea2971cee799.png)

        Duis aute irure dolor in reprehenderit in voluptate velit esse cillum
        dolore eu fugiat nulla pariatur. Excepteur sint occaecat cupidatat non
        proident, sunt in culpa qui officia deserunt mollit anim id est laborum.

        ![Copilot Icon](https://github.githubassets.com/assets/copilot-5a9b7f3d5c64.png)

        Curabitur pretium tincidunt lacus. Nulla gravida orci a odio. Nullam
        varius, turpis molestie dictum semper, arcu felis fermentum metus.

        ![Primer Octicons](https://github.githubassets.com/assets/primer-octicons.svg)

        Aliquam erat volutpat. Nam dui ligula, fringilla a, euismod sodales,
        sollicitudin vel, wisi. Morbi auctor lorem non justo.

        > **Tip:** Use the **Scroll Anchor** sample to see how the layout
        > re-stabilises after lazy images load and change the document height.

        """;

    private const string ScrollAnchorSample = """
        # Scroll Anchoring

        When a lazy image finishes loading, the document height may change.
        Without scroll anchoring, the reading position would jump. With it,
        the renderer captures the first visible block before re-laying-out
        and restores the scroll offset so the user's reading position is
        preserved.

        **How to observe this:**
        1. Switch to the **Lazy Images** sample and scroll part-way down.
        2. The images above the fold finish loading and expand the document.
        3. Your reading position stays stable — the text you were reading
           doesn't move, even though the document height changed.

        ---

        ## Implementation details

        The anchor is captured in `RebuildInternalAsync` just before the
        old snapshot is disposed:

        ```csharp
        // Before snapshot swap
        if (scroll.VerticalOffset > 0)
        {
            foreach (var b in prevSnapshot.Blocks)
            {
                if (b.Bounds.Bottom >= scroll.VerticalOffset)
                {
                    anchor = (b.BlockIndex, b.Bounds.Top - scroll.VerticalOffset);
                    break;
                }
            }
        }
        ```

        After the new snapshot is committed and `_canvas.Height` updated,
        the corresponding block is located in the new layout and the offset
        restored with `ScrollViewer.ChangeView(disableAnimation: true)`.

        The animation is disabled so there is **no visual flash** — the
        viewport jumps instantly to the correct position before the next
        frame is painted.

        ---

        This sample intentionally has no images. Use **Lazy Images** to see
        anchoring in action.
        """;

    private const string FootnotesSample = """
        # Footnote Back-links

        Footnotes are rendered with clickable superscript markers that scroll
        to the footnote definition. Each definition also has a **↩** back-link
        that scrolls back to the inline citation.

        ---

        Here is a sentence with a footnote.[^1]

        And here is another with two more.[^2][^3]

        A longer paragraph that contains a reference to the first footnote
        again.[^1] And ends with a reference to the fourth.[^4]

        ---

        ## More content below footnotes

        This paragraph exists to push the footnote section further down the
        page so that clicking ↩ demonstrates scrolling back upward.

        Lorem ipsum dolor sit amet, consectetur adipiscing elit. Sed do eiusmod
        tempor incididunt ut labore et dolore magna aliqua. Ut enim ad minim
        veniam, quis nostrud exercitation ullamco laboris nisi ut aliquip ex
        ea commodo consequat.

        Duis aute irure dolor in reprehenderit in voluptate velit esse cillum
        dolore eu fugiat nulla pariatur. Excepteur sint occaecat cupidatat non
        proident, sunt in culpa qui officia deserunt mollit anim id est laborum.

        ---

        [^1]: This is the **first** footnote. Click ↩ to return to the text.
        [^2]: Second footnote with `inline code`. Click ↩ to go back.
        [^3]: Third footnote — [a link](https://github.com). Click ↩ to return.
        [^4]: The fourth footnote contains a longer explanation that wraps onto
              multiple lines to demonstrate that the back-link renders correctly
              even in multi-line footnote definitions. Click ↩ to return.
        """;

    private const string KeyboardNavSample = """
        # Keyboard Navigation

        The renderer supports full keyboard navigation without a mouse:

        | Key | Action |
        |-----|--------|
        | **Tab** | Move focus to the next link or embedded control |
        | **Shift+Tab** | Move focus to the previous link or embedded control |
        | **Enter** or **Space** | Activate the focused link (fires `LinkClick`) |
        | **Escape** | Clear the keyboard focus ring (or text selection) |
        | **Ctrl+A** | Select all text |
        | **Ctrl+C** | Copy selected text as markdown |

        ---

        ## Links to navigate with Tab

        Click in the renderer below to give it keyboard focus, then press
        **Tab** to cycle through the links and **Enter** to follow one.

        1. [GitHub](https://github.com) — open source home
        2. [Microsoft](https://microsoft.com) — WinUI and Win2D
        3. [Markdig](https://github.com/xoofx/markdig) — the markdown parser
        4. [Win2D](https://github.com/microsoft/Win2D) — DirectWrite canvas

        ---

        > **Tip:** A focus ring (accent-colored border) appears around the
        > currently focused link. Pressing Escape clears it and returns focus
        > traversal to the start.

        ---

        ## Footnotes are also keyboard-accessible[^kn1][^kn2]

        Use Tab to reach the superscript ¹ or ² markers and press Enter to
        jump to the corresponding footnote definition. From the definition,
        Tab to the ↩ link and press Enter to return.

        [^kn1]: First keyboard-nav footnote.
        [^kn2]: Second keyboard-nav footnote. Press Enter on ↩ to return.
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


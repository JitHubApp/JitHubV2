using System;
using System.Collections.Generic;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Gfm;
using MarkdownRenderer.GitHub;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Html;
using MarkdownRenderer.Layout.Internal.Building;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Math;
using MarkdownRenderer.Mermaid;
using MarkdownRenderer.Parsing.Internal;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Theming.Internal;
using MarkdownRenderer.Theming.Internal.Environment;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.UI;
using Windows.UI.Text;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// The physical TouchSelection sample document, engine and default light theme
/// that <see cref="MarkdownRangeFromPointTouchSelectionRegressionTests"/> lays out
/// headlessly.
/// </summary>
internal static class TouchSelectionSampleFixture
{
    // Match the sample's default light MarkdownTheme rather than supplying a
    // convenient but materially different 16-DIP body style.  Geometry around
    // a captured point depends on headings, tables, quotes, lists, and code
    // blocks preceding the target as much as on the target paragraph itself.
    private static readonly string[] SampleDefaultThemeRoles =
    [
        MarkdownElementKeys.Heading1, MarkdownElementKeys.Heading2,
        MarkdownElementKeys.Heading3, MarkdownElementKeys.Heading4,
        MarkdownElementKeys.Heading5, MarkdownElementKeys.Heading6,
        MarkdownElementKeys.Body, MarkdownElementKeys.CodeInline,
        MarkdownElementKeys.CodeBlock, MarkdownElementKeys.CodeBlockHeader,
        MarkdownElementKeys.CodeBlockLanguage, MarkdownElementKeys.CodeBlockGutter,
        MarkdownElementKeys.CodeBlockLineNumber, MarkdownElementKeys.Quote,
        MarkdownElementKeys.Link, MarkdownElementKeys.Strong,
        MarkdownElementKeys.Emphasis, MarkdownElementKeys.Strikethrough,
        MarkdownElementKeys.Subscript, MarkdownElementKeys.Superscript,
        MarkdownElementKeys.Inserted, MarkdownElementKeys.Marked,
        MarkdownElementKeys.Abbreviation, MarkdownElementKeys.ListMarker,
        MarkdownElementKeys.ThematicBreak, MarkdownElementKeys.ImageCaption,
        MarkdownElementKeys.Figure, MarkdownElementKeys.FigureCaption,
        MarkdownElementKeys.Diagram, MarkdownElementKeys.Math,
        MarkdownElementKeys.DefinitionTerm, MarkdownElementKeys.DefinitionDescription,
        MarkdownElementKeys.Table, MarkdownElementKeys.TableHeader,
        MarkdownElementKeys.TableCell, MarkdownElementKeys.AlertNote,
        MarkdownElementKeys.AlertTip, MarkdownElementKeys.AlertImportant,
        MarkdownElementKeys.AlertWarning, MarkdownElementKeys.AlertCaution,
    ];

    public const string Source = """
        # Touch selection

        Use one finger to scroll. **Press and hold a word** or **double-tap it**
        to select it, then drag either round handle to extend the range. Release
        a long press—or tap inside an existing selection—to open the native Copy,
        Copy as Markdown, and Select All menu.

        A plain tap keeps its normal meaning: [this link remains touchable](https://example.invalid/touch-link),
        while tapping elsewhere clears an active selection.

        ## Unicode and mixed direction

        Drag across emoji and combining text without splitting a visible character:
        👩🏽‍💻 family 👨‍👩‍👧‍👦 and café written as café.

        Mixed bidi text remains logically selectable: English before العربية שלום
        and English after. اسحب مقابض التحديد عبر هذا السطر لاختبار الاتجاه المختلط.

        ## Formatting boundaries

        Selection crosses **bold words**, *emphasized words*, `inline code`, and
        [linked labels](https://learn.microsoft.com/windows/apps/design/input/).

        | Surface | Expected touch behavior |
        |---|---|
        | Body text | Hold or double-tap selects a word |
        | Link label | Hold selects; tap activates |
        | Table text | Handles cross cell content atomically |
        | Hosted control | Receives direct touch input |
        | Diagram | Non-selectable regions remain direct interaction targets |

        - [ ] Native task checkbox remains independently touchable

        ```button:Touch_hosted_action
        ```

        ```csharp
        // Code remains selectable, while its Copy button remains a native control.
        string greeting = "Touch selection stays grapheme-safe";
        ```

        ```mermaid
        flowchart LR
            Pan[One-finger pan] --> Hold[Long press]
            Hold --> Word[Word selection]
            Word --> Handles[44 epx handles]
            Handles --> Copy[Native menu]
        ```

        ## Edge-scroll exercise

        Select a word above, drag the end handle into the bottom edge band, and
        hold your finger still. The viewport must continue scrolling while the
        endpoint extends. Repeat upward after crossing the handles.

        1. This paragraph provides distance for continuous edge scrolling.
        2. Selection must continue across list items and inline formatting.
        3. The document surface must not relayout or repaint its tiles per frame.
        4. Scroll inertia must remain owned by the platform ScrollViewer.
        5. Canceling the gesture must leave no capture, timer, flyout, or glyph residue.

        > Touch testing should also cover Light, Dark, Windows High Contrast,
        > 100%, 150%, and 200% display scaling, narrow snap widths, and RTL flow.

        Final selection target: **stationary edge dragging continues here**.
        """;

    public static MarkdownEngine CreateEngine() => new MarkdownEngineBuilder()
        .UseGitHubReadme(SafeHtmlOptions.Default)
        .UseMarkdownExtra()
        .UseMathematics()
        .UseMermaid()
        .UseExtension(TouchSelectionHostedElementExtension.Instance)
        .Build();

    public static MarkdownLayoutContext CreateContext(
        CanvasDevice device,
        MarkdownExtensionRegistry registry)
    {
        PlatformThemeColors platformColors =
            PlatformThemeColors.Resolve(ElementTheme.Light, default);
        FontWeight semiBold = new() { Weight = 600 };
        var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal);
        foreach (string role in SampleDefaultThemeRoles)
        {
            styles.Add(
                role,
                ThemeDefaultStyles.CreateDefaultStyle(
                    role,
                    isDark: false,
                    isHighContrast: false,
                    accentColor: null,
                    platformColors: platformColors,
                    semiBoldFontWeight: semiBold,
                    systemTheme: null));
        }

        Color transparent = Color.FromArgb(0, 0, 0, 0);
        var theme = new ThemeSnapshot(
            styles,
            new Dictionary<string, ElementStyleOverride>(StringComparer.Ordinal),
            transparent,
            transparent,
            transparent,
            transparent,
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1,
            styleSheet: null);
        return new MarkdownLayoutContext(
            device,
            theme,
            new MarkdownSourceMap(Source),
            registry,
            FlowDirection.LeftToRight);
    }

    public static LayoutSnapshot BuildSnapshot(
        MarkdownLayoutContext context,
        MarkdownRenderer.Document.MarkdownDocument parsed,
        float width) => new LayoutBuilder(
            context,
            enableDeclarativeHostedElements: true,
            semanticDocument: parsed)
        .Build(parsed.ParsedDocument!, width);
}

using System;
using System.Threading;

namespace MarkdownRenderer.Sample;

internal static partial class SampleDocuments
{
    private static readonly Lazy<string> VirtualizationSampleValue = new(
        static () => """
            # Embed virtualization

            This sample renders a long document with many hosted WinUI button
            embeds (300 below). Only the embeds in the visible viewport are
            instantiated; off-screen ones are torn down and recreated when they
            scroll back into view. Watch the memory usage stay flat while
            scrolling — the renderer caps realised embeds to a bounded set.

            Tip: the realisation band extends ±400 px around the viewport, and
            the de-realisation band ±1200 px, providing hysteresis so embeds
            near the edge don't thrash.

            """ + GenerateVirtualizationButtons(300),
        LazyThreadSafetyMode.ExecutionAndPublication);

    internal static string VirtualizationSample => VirtualizationSampleValue.Value;

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

    private static readonly Lazy<string> StressSampleValue = new(
        GenerateStressSample,
        LazyThreadSafetyMode.ExecutionAndPublication);

    internal static string StressSample => StressSampleValue.Value;

    private static string GenerateStressSample()
    {
        var sb = new System.Text.StringBuilder(800_000);
        sb.AppendLine("# Long Document Stress");
        sb.AppendLine();
        sb.AppendLine("This page mixes headings, paragraphs, lists, tables, code, footnotes, images, and embeds so scroll, selection, theme switching, and lazy realization can be exercised together.");
        sb.AppendLine();
        for (int i = 1; i <= 1_200; i++)
        {
            if (i % 40 == 1)
            {
                sb.Append("## Section ").Append((i / 40) + 1).AppendLine();
                sb.AppendLine();
            }

            sb.Append("Paragraph ").Append(i).Append(": ");
            sb.AppendLine("The quick brown fox jumps over the lazy dog with **bold**, *italic*, `code`, [a link](https://example.com), H~2~O, E = mc^2^, ++inserted++, and ==marked== text for layout stress.");
            sb.AppendLine();

            if (i % 7 == 0)
            {
                sb.Append("- [").Append(i % 14 == 0 ? "x" : " ").Append("] Task item ").Append(i).AppendLine();
                sb.Append("  - Nested child ").Append(i).AppendLine();
                sb.AppendLine();
            }

            if (i % 25 == 0)
            {
                sb.AppendLine("| Left | Center | Right |");
                sb.AppendLine("|:---|:---:|---:|");
                for (int row = 0; row < 5; row++)
                    sb.Append("| L").Append(i).Append('-').Append(row).Append(" | C | R |").AppendLine();
                sb.AppendLine();
            }

            if (i % 60 == 0)
            {
                sb.AppendLine("```csharp");
                sb.Append("Console.WriteLine(\"stress ").Append(i).AppendLine("\");");
                sb.AppendLine("```");
                sb.AppendLine();
            }

            if (i % 90 == 0)
            {
                sb.Append("```button:Stress action ").Append(i).AppendLine();
                sb.AppendLine("```");
                sb.AppendLine();
            }
        }

        sb.AppendLine("Footnote check[^stress].");
        sb.AppendLine();
        sb.AppendLine("[^stress]: End-of-document footnote for navigation and selection stress.");
        return sb.ToString();
    }

    internal const string AccessibilityLabSample = """
        # Accessibility Lab

        This stable page is intentionally dense: it contains semantic roles,
        text ranges, hosted elements, and bidi cases that UI
        automation probes use for accessibility verification.

        ## Text Pattern

        The quick brown fox jumps over the lazy dog. Narrator should read this
        paragraph as document text, and UIA TextPattern ranges should return
        word bounding rectangles for each word.

        Inline `inline-code-token` text must remain visible in high contrast.

        Inline image ![Inline accessibility icon](data:image/svg+xml;utf8,%3Csvg%20xmlns%3D%27http%3A%2F%2Fwww.w3.org%2F2000%2Fsvg%27%20width%3D%2716%27%20height%3D%2716%27%20viewBox%3D%270%200%2016%2016%27%3E%3Ctitle%3EInline%20icon%3C%2Ftitle%3E%3Ccircle%20cx%3D%278%27%20cy%3D%278%27%20r%3D%276%27%20fill%3D%27%230078D4%27%2F%3E%3C%2Fsvg%3E) should expose image semantics inside paragraph text.

        A [painted link](https://example.com/accessibility) appears before two
        declarative hosted-element syntax fixtures. This sample installs their
        stable runtime adapter, so UI Automation sees the native controls while
        document text retains their semantic labels.

        ```button:Native_action
        ```

        ```panel:Composite_action
        ```

        ```styled:Ancestor-scoped_extension_style
        ```

        The next fixture deliberately uses a factory key the sample host declines.
        It must atomically return to the original native code-block rendering.

        ```unsupported:Fallback_probe
        native hosted fallback marker
        ```

        - [ ] Read-only task state after the button (no `TogglePattern`)
        - A regular list item with a [second painted link](https://example.com/second)
        - Mixed bidi text: English אבגדה Arabic مرحبا 12345

        ## Semantic Table

        | Feature | Expected UIA Role | Detail |
        |---------|-------------------|--------|
        | Table | Table/Grid | Exposes row and column counts |
        | Header cells | DataItem with headers | Header row is discoverable |
        | Body cells | DataItem | Cell coordinates are stable |

        ## Image

        ![Accessibility lab blue square](data:image/svg+xml;utf8,%3Csvg%20xmlns%3D%27http%3A%2F%2Fwww.w3.org%2F2000%2Fsvg%27%20width%3D%2748%27%20height%3D%2748%27%20viewBox%3D%270%200%2048%2048%27%3E%3Ctitle%3EBlue%20square%3C%2Ftitle%3E%3Cdesc%3EUsed%20for%20accessibility%20automation%3C%2Fdesc%3E%3Crect%20width%3D%2748%27%20height%3D%2748%27%20fill%3D%27%230078D4%27%2F%3E%3C%2Fsvg%3E)

        ## Code

        ```csharp
        Console.WriteLine("code language is exposed as help text");
        ```

        ## Offscreen Content

        ScrollIntoView should bring this later content into view through UIA.

        Paragraph 1. More stable text for range movement.

        Paragraph 2. More stable text for range movement.

        Paragraph 3. More stable text for range movement.

        Paragraph 4. More stable text for range movement.

        Paragraph 5. More stable text for range movement.
        """;

    internal const string AuditMatrixSample = """
        # Markdown audit matrix

        This deterministic document exercises native rendering without requiring network access.

        > A bounded quote containing **strong text**, `inline code`, and a
        > [keyboard link](https://example.com/audit-link).

        - [x] Completed audit task
        - [ ] Pending audit task

        The two task states above are noninteractive semantics. UI Automation
        must not advertise `TogglePattern` while editable mode is disabled.

        | Surface | Expected behavior |
        |---------|-------------------|
        | Table | Remains readable and selectable |
        | Images | Never escape the document viewport |

        The link-free table below has more minimum-width columns than the
        viewport. It is a deterministic keyboard-only overflow fixture.

        | 01 | 02 | 03 | 04 | 05 | 06 | 07 | 08 | 09 | 10 | 11 | 12 | 13 | 14 | 15 | 16 | 17 | 18 | 19 | 20 | 21 | 22 | 23 | 24 | 25 | 26 | 27 | 28 | 29 | 30 | 31 | 32 | 33 | 34 | 35 | 36 | 37 | 38 | 39 | 40 | 41 | 42 | 43 | 44 | 45 | 46 | 47 | 48 |
        |----|----|----|----|----|----|----|----|----|----|----|----|----|----|----|----|----|----|----|----|----|----|----|----|----|----|----|----|----|----|----|----|----|----|----|----|----|----|----|----|----|----|----|----|----|----|----|----|
        | aa | bb | cc | dd | ee | ff | gg | hh | ii | jj | kk | ll | mm | nn | oo | pp | qq | rr | ss | tt | uu | vv | ww | xx | yy | zz | a1 | b2 | c3 | d4 | e5 | f6 | g7 | h8 | i9 | j0 | k1 | l2 | m3 | n4 | o5 | p6 | q7 | r8 | s9 | t0 |

        ```csharp
        public static string Audit() => "selection and copy remain available";
        ```

        ## Safe inline image

        ![Safe blue audit square](data:image/svg+xml;utf8,%3Csvg%20xmlns%3D%27http%3A%2F%2Fwww.w3.org%2F2000%2Fsvg%27%20width%3D%2732%27%20height%3D%2732%27%20viewBox%3D%270%200%2032%2032%27%3E%3Crect%20width%3D%2732%27%20height%3D%2732%27%20fill%3D%27%230078D4%27%2F%3E%3C%2Fsvg%3E)

        ## Decorative image

        The empty-alt image below is decorative and must stay out of the semantic
        and UI Automation trees.

        ![](data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw==)

        ## Animated image input

        ![Animated image audit fixture](data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH/C05FVFNDQVBFMi4wAwEAAAAh+QQFCgAAACwAAAAAAQABAAACAkQBADs=)

        ## Hostile and malformed content

        The invalid formula below must fall back to exact source text and expose
        a recoverable `MATH100` diagnostic without disrupting the rest of the
        committed document:

        $\notacommand{sample}$

        The unsupported Mermaid layout below must likewise preserve its exact
        source and expose a recoverable `MMR0002` diagnostic. ELK is deliberately
        excluded from the native package:

        ```mermaid
        ---
        config:
          layout: elk
        ---
        flowchart LR
          C --> D
        ```

        ![Malformed SVG audit fixture](data:image/svg+xml;utf8,%3Csvg%20xmlns%3D%27http%3A%2F%2Fwww.w3.org%2F2000%2Fsvg%27%20width%3D%279999999%27%20height%3D%279999999%27%3E%3Ctext%20style%3D%27font-size%3A999999px%27%3Eoversized%3C%2Ftext%3E)

        <svg width="9999999" height="9999999"><text style="font-size:999999px">raw hostile svg text</text>
        <img src="javascript:alert('blocked')" onerror="alert('blocked')">

        ## Final marker

        Document remains responsive after hostile content. Select this sentence and copy it.
        """;
}

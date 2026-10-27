using System;
using System.Text;

namespace MarkdownRenderer.Sample;

internal static partial class SampleDocuments
{
    internal static string SvgStressSample
    {
        get
        {
            string themed = SvgData("""
                <svg xmlns="http://www.w3.org/2000/svg" width="560" height="150" viewBox="0 0 560 150">
                  <desc>Three theme-aware cards demonstrating CSS, currentColor, gradients, masks, markers, and filters.</desc>
                  <style>
                    .card { stroke: currentColor; stroke-width: 3; }
                    .label { fill: currentColor; font: 600 18px 'Segoe UI', sans-serif; }
                    @media (prefers-color-scheme: light) { .card { fill: #e8f2ff; } }
                    @media (prefers-color-scheme: dark) { .card { fill: #17243a; } }
                  </style>
                  <defs>
                    <linearGradient id="g" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#32d583"/><stop offset="1" stop-color="#1570ef"/></linearGradient>
                    <pattern id="p" width="12" height="12" patternUnits="userSpaceOnUse"><path d="M0 12L12 0" stroke="#7f56d9" stroke-width="3"/></pattern>
                    <marker id="arrow" markerWidth="8" markerHeight="8" refX="7" refY="4" orient="auto"><path d="M0 0L8 4L0 8Z" fill="currentColor"/></marker>
                    <mask id="m"><rect width="100%" height="100%" fill="white"/><circle cx="462" cy="66" r="22" fill="black"/></mask>
                    <filter id="shadow" x="-30%" y="-30%" width="160%" height="160%"><feDropShadow dx="0" dy="5" stdDeviation="4" flood-color="#000" flood-opacity=".28"/></filter>
                  </defs>
                  <rect class="card" x="10" y="12" width="165" height="120" rx="18"/><circle cx="92" cy="64" r="34" fill="url(#g)" filter="url(#shadow)"/><text class="label" x="55" y="116">gradient</text>
                  <rect class="card" x="196" y="12" width="165" height="120" rx="18"/><rect x="220" y="34" width="117" height="55" rx="10" fill="url(#p)"/><path d="M220 105H330" stroke="currentColor" stroke-width="4" marker-end="url(#arrow)"/>
                  <rect class="card" x="382" y="12" width="165" height="120" rx="18"/><g mask="url(#m)"><rect x="403" y="34" width="120" height="64" rx="14" fill="url(#g)"/></g><text class="label" x="421" y="121">mask</text>
                </svg>
                """);

            string text = SvgData("""
                <svg xmlns="http://www.w3.org/2000/svg" width="720" height="185" viewBox="0 0 720 185">
                  <desc>Unicode, bidirectional, emoji, transformed, clipped, and text-path examples rendered by resvg.</desc>
                  <defs><path id="curve" d="M25 132 C180 32 350 210 520 90"/><clipPath id="clip"><rect x="535" y="18" width="165" height="145" rx="22"/></clipPath></defs>
                  <rect width="720" height="185" rx="24" fill="#0f172a"/>
                  <text x="24" y="38" fill="#f8fafc" font-family="Segoe UI" font-size="21">Latin • Ελληνικά • 日本語 • é • 👩‍💻</text>
                  <text x="500" y="72" fill="#fbbf24" font-family="Segoe UI" font-size="24" text-anchor="end">مرحبا بالعالم</text>
                  <text fill="#5eead4" font-family="Segoe UI" font-size="18"><textPath href="#curve">Text follows a transformed Bézier path — مسار النص</textPath></text>
                  <g clip-path="url(#clip)" transform="rotate(-8 615 90)"><rect x="520" y="5" width="210" height="175" fill="#7c3aed"/><text x="552" y="100" fill="white" font-family="Segoe UI" font-size="25">clipped</text></g>
                </svg>
                """);

            const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";
            string embedded = SvgData($$"""
                <svg xmlns="http://www.w3.org/2000/svg" width="620" height="120" viewBox="0 0 620 120">
                  <desc>Repeated raster payloads and one nested SVG exercise decoded-resource deduplication.</desc><rect width="620" height="120" rx="18" fill="#f2f4f7"/>
                  <image href="data:image/png;base64,{{png}}" x="20" y="20" width="80" height="80"/><image href="data:image/png;base64,{{png}}" x="115" y="20" width="80" height="80"/><image href="data:image/png;base64,{{png}}" x="210" y="20" width="80" height="80"/>
                  <image href="data:image/svg+xml;base64,PHN2ZyB4bWxucz0naHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmcnIHZpZXdCb3g9JzAgMCAxMDAgMTAwJz48Y2lyY2xlIGN4PSc1MCcgY3k9JzUwJyByPSc0MicgZmlsbD0nIzE1NzBlZicvPjxwYXRoIGQ9J00yNSA1MGwxNSAxNSA0MC00MCcgc3Ryb2tlPSd3aGl0ZScgc3Ryb2tlLXdpZHRoPScxMicgZmlsbD0nbm9uZScvPjwvc3ZnPg==" x="315" y="10" width="100" height="100"/>
                  <text x="440" y="67" fill="#101828" font-family="Segoe UI" font-size="18">deduplicated resources</text>
                </svg>
                """);

            string wide = SvgData("""
                <svg xmlns="http://www.w3.org/2000/svg" width="1600" height="220" viewBox="0 0 1600 220" preserveAspectRatio="xMidYMid meet"><desc>A very wide image that must shrink inside its table cell without widening the document.</desc><defs><linearGradient id="w"><stop stop-color="#1570ef"/><stop offset="1" stop-color="#12b76a"/></linearGradient></defs><rect width="1600" height="220" rx="28" fill="url(#w)"/><path d="M80 150 C340 15 600 205 850 70 S1320 20 1520 125" fill="none" stroke="white" stroke-width="18"/></svg>
                """);
            string icon = SvgData("""
                <svg xmlns="http://www.w3.org/2000/svg" width="72" height="72" viewBox="0 0 72 72"><circle cx="36" cy="36" r="31" fill="currentColor" opacity=".18"/><path d="M20 37l10 10 22-24" fill="none" stroke="currentColor" stroke-width="7" stroke-linecap="round" stroke-linejoin="round"/></svg>
                """);
            string forbidden = SvgData("""
                <svg xmlns="http://www.w3.org/2000/svg" width="180" height="60"><script>alert(1)</script><rect width="180" height="60" fill="red"/></svg>
                """);
            string external = SvgData("""
                <svg xmlns="http://www.w3.org/2000/svg" width="180" height="60"><image href="https://example.invalid/nested.png" width="180" height="60"/></svg>
                """);
            string animation = SvgData("""
                <svg xmlns="http://www.w3.org/2000/svg" width="180" height="60"><circle cx="30" cy="30" r="20"><animate attributeName="cx" to="150" dur="1s"/></circle></svg>
                """);

            var storm = new StringBuilder();
            for (int index = 0; index < 12; index++)
            {
                storm.Append("![Repeated cache-test icon ").Append(index + 1).Append("](").Append(icon).Append(") ");
            }

            return $$"""
                # Browser-class static SVG

                This page exercises the isolated resvg provider through the same markdown image pipeline used by applications. Change app theme, Windows contrast theme, text scale, display scale, viewport width, and viewport ownership while scrolling and selecting.

                ## CSS, semantic color, paint servers, masks, markers, and filters

                ![Theme-aware SVG feature matrix]({{themed}})

                ## Text, Unicode, RTL, transforms, clipping, and text paths

                ![Unicode and bidirectional SVG text]({{text}})

                ## Bounded embedded images and nested SVG

                ![Embedded-resource deduplication]({{embedded}})

                ## Table containment and unusual aspect ratio

                | Shrinkable SVG | Adjacent content remains visible |
                | --- | --- |
                | ![Wide graph constrained to its table cell]({{wide}}) | The intrinsic 1600-pixel width must not become an unshrinkable column minimum. |

                ## Cache and visible-queue storm

                The twelve identical sources below should deduplicate open/render work and remain responsive.

                {{storm}}

                ## Deliberate accessible failure states

                Each unsafe input must render one atomic diagnostic placeholder, never partial artwork.

                | Input | Expected result |
                | --- | --- |
                | ![Script-bearing SVG]({{forbidden}}) | Unsupported-content placeholder |
                | ![Nested external image SVG]({{external}}) | External-resource placeholder |
                | ![Animated SVG]({{animation}}) | Unsupported-content placeholder |

                ## Decorative semantics

                The next SVG has explicit empty alt text and must not enter the content view.

                ![]({{icon}})

                ## Last-item selection boundary

                This final image must be selectable by dragging into its visible bounds; dragging below the document must not be required.

                ![Final selectable SVG with a check mark]({{icon}})
                """;
        }
    }

    private static string SvgData(string svg) =>
        "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));
}

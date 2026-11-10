using System.Text;
using MarkdownRenderer.Svg.Resvg.Internal.Preparation;
using MarkdownRenderer.Svg.Resvg.Internal.StaticSnapshots;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

/// <summary>
/// Pins the static snapshot transform: SMIL initial frames, script and
/// motion removal, foreignObject conversion, switch fallback, and font-face
/// removal, each followed by resource-budget validation of the result.
/// </summary>
[Collection(WorkerDeadlineSensitiveCollection.Name)]
public sealed class SvgStaticSnapshotTransformTests
{
    [Fact]
    public void StaticSnapshot_ConvertsSmilToDeterministicInitialFrame()
    {
        byte[] source = Bytes(
            "<svg xmlns='http://www.w3.org/2000/svg'>" +
            "<g opacity='0'><animate attributeName='opacity' values='0;0;1;1' keyTimes='0;0;0;.5'/></g>" +
            "<g transform='translate(1 1)'><animateTransform attributeName='transform' type='translate' values='0,48;0,48;0,0' keyTimes='0;0;0' additive='sum'/></g>" +
            "</svg>");

        byte[] snapshot = SvgStaticSnapshot.Create(source, CancellationToken.None);
        string text = Encoding.UTF8.GetString(snapshot);
        SvgResourceBudgetResult result = SvgResourceBudget.Validate(snapshot, CancellationToken.None);

        Assert.True(result.Accepted, result.Reason);
        Assert.DoesNotContain("<animate", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("opacity=\"1\"", text, StringComparison.Ordinal);
        Assert.Contains("transform=\"translate(1 1) translate(0,0)\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void StaticSnapshotUtility_RemovesSvgScriptAndAnimationMarkupWhenInvokedInIsolation()
    {
        // This characterizes the optional snapshot transform only. Provider
        // admission validates the original source first and rejects these bytes.
        byte[] source = Bytes(
            "<svg><script>alert(1)</script><rect><animate attributeName='opacity' from='0'/></rect></svg>");

        byte[] snapshot = SvgStaticSnapshot.Create(source, CancellationToken.None);
        string text = Encoding.UTF8.GetString(snapshot);
        SvgResourceBudgetResult result = SvgResourceBudget.Validate(snapshot, CancellationToken.None);

        Assert.True(result.Accepted, result.Reason);
        Assert.DoesNotContain("<script", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<animate", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("opacity=\"0\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void StaticSnapshotUtility_RemovesSizingScriptAndPreservesAuthoredGeometry()
    {
        // This does not assert that the transformed badge is admitted or
        // rendered; ResvgMarkdownSvgRenderer.PrepareSource rejects the original.
        byte[] source = Bytes(
            "<svg xmlns='http://www.w3.org/2000/svg' width='119.46' height='20'>" +
            "<rect width='119.46' height='20' fill='#555'/>" +
            "<text x='32.07' y='14'>user base</text>" +
            "<script type='text/javascript'>document.currentScript.closest('svg').setAttribute('width', 1)</script>" +
            "</svg>");

        byte[] snapshot = SvgStaticSnapshot.Create(source, CancellationToken.None);
        string text = Encoding.UTF8.GetString(snapshot);
        SvgResourceBudgetResult result = SvgResourceBudget.Validate(snapshot, CancellationToken.None);

        Assert.True(result.Accepted, result.Reason);
        Assert.DoesNotContain("<script", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("width=\"119.46\"", text, StringComparison.Ordinal);
        Assert.Contains("user base", text, StringComparison.Ordinal);
    }

    [Fact]
    public void StaticSnapshot_ConvertsTextOnlyHtmlForeignObject()
    {
        byte[] source = Bytes(
            "<svg xmlns='http://www.w3.org/2000/svg'>" +
            "<foreignObject x='6' y='10' width='198' height='17' " +
            "selection='true' style='font-size:9px;color:rgb(67, 39, 135);font-family:Arial;font-weight:400;text-align:center;letter-spacing:0em;line-height:1.5'>" +
            "<div xmlns='http://www.w3.org/1999/xhtml'>GITHUB TRENDING</div>" +
            "</foreignObject></svg>");

        byte[] snapshot = SvgStaticSnapshot.Create(source, CancellationToken.None);
        string text = Encoding.UTF8.GetString(snapshot);
        SvgResourceBudgetResult result = SvgResourceBudget.Validate(snapshot, CancellationToken.None);

        Assert.True(result.Accepted, result.Reason);
        Assert.DoesNotContain("foreignObject", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GITHUB TRENDING", text, StringComparison.Ordinal);
        Assert.Contains("text-anchor=\"middle\"", text, StringComparison.Ordinal);
        Assert.Contains("fill=\"rgb(67, 39, 135)\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void StaticSnapshot_ConvertsSafeD2TableForeignObject()
    {
        byte[] source = Bytes(
            "<svg xmlns='http://www.w3.org/2000/svg'><foreignObject " +
            "requiredFeatures='http://www.w3.org/TR/SVG11/feature#Extensibility' " +
            "x='30' y='40' width='382' height='113'>" +
            "<div xmlns='http://www.w3.org/1999/xhtml' class='md color-N1'><table>" +
            "<tbody><tr><td style='width:100%'><h6 style='margin:0;font-size:1em;color:inherit'>Core</h6></td>" +
            "<td align='right' style='white-space:nowrap'>Rust</td></tr>" +
            "<tr><td colspan='2'>/packages/core/</td></tr>" +
            "<tr><td colspan='2'>LocalSend protocol library</td></tr>" +
            "</tbody></table></div></foreignObject></svg>");

        byte[] snapshot = SvgStaticSnapshot.Create(source, CancellationToken.None);
        string text = Encoding.UTF8.GetString(snapshot);
        SvgResourceBudgetResult result = SvgResourceBudget.Validate(snapshot, CancellationToken.None);

        Assert.True(result.Accepted, result.Reason);
        Assert.DoesNotContain("foreignObject", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Core Rust /packages/core/ LocalSend protocol library", text, StringComparison.Ordinal);
    }

    [Fact]
    public void StaticSnapshot_ConvertsBoundedMetricsCardForeignObject()
    {
        byte[] source = Bytes(
            "<svg xmlns='http://www.w3.org/2000/svg' width='720' height='110' viewBox='0 0 720 110'>" +
            "<foreignObject x='0' y='0' width='100%' height='100%'>" +
            "<div xmlns='http://www.w3.org/1999/xhtml'><section><h2>Status</h2>" +
            "<svg xmlns='http://www.w3.org/2000/svg' width='16' height='16'><path d='M0 0h16v16z'/></svg>" +
            "<span>461 open 1309 closed</span></section><div id='metrics-end'></div></div></foreignObject></svg>");

        byte[] snapshot = SvgStaticSnapshot.Create(source, CancellationToken.None);
        string text = Encoding.UTF8.GetString(snapshot);
        SvgResourceBudgetResult result = SvgResourceBudget.Validate(snapshot, CancellationToken.None);

        Assert.True(result.Accepted, result.Reason);
        Assert.DoesNotContain("foreignObject", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Status 461 open 1309 closed", text, StringComparison.Ordinal);
        Assert.Contains("x=\"0\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void StaticSnapshot_ConvertsStylesheetBackedBadgeForeignObject()
    {
        byte[] source = Bytes(
            "<svg xmlns='http://www.w3.org/2000/svg' width='132' height='20'>" +
            "<foreignObject class='star-svg-obj' x='0' y='0' width='100%' height='100%'>" +
            "<style>.box{display:flex;background:#fbefd8}.label{font:12px Arial}</style>" +
            "<div xmlns='http://www.w3.org/1999/xhtml' class='box'>" +
            "<div><svg xmlns='http://www.w3.org/2000/svg' width='61' height='16'>" +
            "<rect width='61' height='16' fill='#e9a014'/></svg></div>" +
            "<div class='label'><span>472</span><span> Stars</span></div>" +
            "</div></foreignObject></svg>");

        byte[] snapshot = SvgStaticSnapshot.Create(source, CancellationToken.None);
        string text = Encoding.UTF8.GetString(snapshot);
        SvgResourceBudgetResult result = SvgResourceBudget.Validate(snapshot, CancellationToken.None);

        Assert.True(result.Accepted, result.Reason);
        Assert.DoesNotContain("foreignObject", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<style", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("472 Stars", text, StringComparison.Ordinal);
    }

    [Fact]
    public void StaticSnapshot_DoesNotHideActiveContentInsideNestedSvgFallback()
    {
        byte[] source = Bytes(
            "<svg xmlns='http://www.w3.org/2000/svg' width='100' height='20'><foreignObject width='100%' height='100%'>" +
            "<div xmlns='http://www.w3.org/1999/xhtml'>text" +
            "<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>" +
            "</div></foreignObject></svg>");

        byte[] snapshot = SvgStaticSnapshot.Create(source, CancellationToken.None);
        SvgResourceBudgetResult result = SvgResourceBudget.Validate(snapshot, CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal("active-content", result.Reason);
    }

    [Fact]
    public void StaticSnapshot_SelectsNativeSvgFallbackFromSwitch()
    {
        byte[] source = Bytes(
            "<svg xmlns='http://www.w3.org/2000/svg'><switch>" +
            "<foreignObject width='100%' height='100%' requiredFeatures='http://www.w3.org/TR/SVG11/feature#Extensibility'>" +
            "<div xmlns='http://www.w3.org/1999/xhtml' onclick='alert(1)'>web label</div>" +
            "</foreignObject><text x='4' y='12'>safe fallback</text></switch></svg>");

        byte[] snapshot = SvgStaticSnapshot.Create(source, CancellationToken.None);
        string text = Encoding.UTF8.GetString(snapshot);
        SvgResourceBudgetResult result = SvgResourceBudget.Validate(snapshot, CancellationToken.None);

        Assert.True(result.Accepted, result.Reason);
        Assert.DoesNotContain("foreignObject", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("safe fallback", text, StringComparison.Ordinal);
    }

    [Fact]
    public void StaticSnapshot_RemovesCssMotionAndKeepsStaticRules()
    {
        byte[] source = Bytes(
            "<svg xmlns='http://www.w3.org/2000/svg'><style>" +
            ".sprite{fill:#e07c4c;animation:jump .5s infinite;transition:opacity .2s}" +
            "@keyframes jump{0%{transform:translateY(0)}50%{transform:translateY(-10px)}}" +
            "</style><rect class='sprite' width='10' height='10'/></svg>");

        byte[] snapshot = SvgStaticSnapshot.Create(source, CancellationToken.None);
        string text = Encoding.UTF8.GetString(snapshot);
        SvgResourceBudgetResult result = SvgResourceBudget.Validate(snapshot, CancellationToken.None);

        Assert.True(result.Accepted, result.Reason);
        Assert.DoesNotContain("@keyframes", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("animation", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("transition", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fill:#e07c4c", text, StringComparison.Ordinal);
    }

    [Fact]
    public void StaticSnapshot_RemovesInlineMotionAndKeepsStaticDeclarations()
    {
        byte[] source = Bytes(
            "<svg xmlns='http://www.w3.org/2000/svg'><style>" +
            "@keyframes fade{from{opacity:0}to{opacity:1}}" +
            ".point{animation:fade .5s ease-out forwards}" +
            "</style><circle class='point' style='fill:#6b63ff; animation-delay: 1.50s' r='4'/></svg>");

        byte[] snapshot = SvgStaticSnapshot.Create(source, CancellationToken.None);
        string text = Encoding.UTF8.GetString(snapshot);
        SvgResourceBudgetResult result = SvgResourceBudget.Validate(snapshot, CancellationToken.None);

        Assert.True(result.Accepted, result.Reason);
        Assert.DoesNotContain("@keyframes", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("animation", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fill:#6b63ff", text, StringComparison.Ordinal);
    }

    [Fact]
    public void StaticSnapshot_DoesNotTreatMotionNamedSelectorsAsDeclarations()
    {
        byte[] source = Bytes(
            "<svg xmlns='http://www.w3.org/2000/svg'><style>" +
            ".animation:hover{fill:#123456}.transition[data-state='ready']{stroke:#abcdef}" +
            "</style><rect class='animation transition' data-state='ready' width='10' height='10'/></svg>");

        byte[] snapshot = SvgStaticSnapshot.Create(source, CancellationToken.None);
        string text = Encoding.UTF8.GetString(snapshot);
        SvgResourceBudgetResult result = SvgResourceBudget.Validate(snapshot, CancellationToken.None);

        Assert.True(result.Accepted, result.Reason);
        Assert.Contains(".animation:hover{fill:#123456}", text, StringComparison.Ordinal);
        Assert.Contains(".transition[data-state='ready']{stroke:#abcdef}", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<img xmlns='http://www.w3.org/1999/xhtml' src='https://example.test/a.png'/>")]
    [InlineData("<script xmlns='http://www.w3.org/1999/xhtml'>alert(1)</script>")]
    [InlineData("<div xmlns='http://www.w3.org/1999/xhtml' onclick='alert(1)'>unsafe</div>")]
    public void StaticSnapshot_DoesNotConvertInteractiveForeignObject(string content)
    {
        byte[] source = Bytes(
            $"<svg xmlns='http://www.w3.org/2000/svg'><foreignObject width='100' height='20'>{content}</foreignObject></svg>");

        byte[] snapshot = SvgStaticSnapshot.Create(source, CancellationToken.None);
        SvgResourceBudgetResult result = SvgResourceBudget.Validate(snapshot, CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal("active-content", result.Reason);
    }

    [Theory]
    [InlineData("onload='alert(1)'")]
    [InlineData("data-unknown='discarded styling'")]
    [InlineData("style='background:url(https://example.test/a.png)'")]
    public void StaticSnapshot_DoesNotHideUnsupportedForeignObjectAttributes(string attributes)
    {
        byte[] source = Bytes(
            $"<svg xmlns='http://www.w3.org/2000/svg'><foreignObject width='100' height='20' {attributes}>" +
            "<div xmlns='http://www.w3.org/1999/xhtml'>text</div></foreignObject></svg>");

        byte[] snapshot = SvgStaticSnapshot.Create(source, CancellationToken.None);
        SvgResourceBudgetResult result = SvgResourceBudget.Validate(snapshot, CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal("active-content", result.Reason);
    }

    [Fact]
    public void StaticSnapshot_RemovesEmbeddedFontFaceAndKeepsChartStyles()
    {
        byte[] source = Bytes(
            "<svg xmlns='http://www.w3.org/2000/svg'><style>" +
            "@font-face{font-family:xkcd;src:url(data:application/font-woff;base64,AAAA)}" +
            ".label{font-family:xkcd;fill:#123456}" +
            "</style><text class='label'>History</text></svg>");

        byte[] snapshot = SvgStaticSnapshot.Create(source, CancellationToken.None);
        string text = Encoding.UTF8.GetString(snapshot);
        SvgResourceBudgetResult result = SvgResourceBudget.Validate(snapshot, CancellationToken.None);

        Assert.True(result.Accepted, result.Reason);
        Assert.DoesNotContain("@font-face", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".label{font-family:xkcd;fill:#123456}", text, StringComparison.Ordinal);
        Assert.Contains("History", text, StringComparison.Ordinal);
    }

    [Fact]
    public void StaticSnapshot_RemovesExternalFontImportAndKeepsFallbackText()
    {
        byte[] source = Bytes(
            "<svg xmlns='http://www.w3.org/2000/svg'><style>" +
            "@import url(https://example.test/font.css);" +
            ".label{font-family:'Source Sans 3',sans-serif;fill:#123456}" +
            "</style><text class='label'>Translations</text></svg>");

        byte[] snapshot = SvgStaticSnapshot.Create(source, CancellationToken.None);
        string text = Encoding.UTF8.GetString(snapshot);
        SvgResourceBudgetResult result = SvgResourceBudget.Validate(snapshot, CancellationToken.None);

        Assert.True(result.Accepted, result.Reason);
        Assert.DoesNotContain("@import", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sans-serif", text, StringComparison.Ordinal);
        Assert.Contains("Translations", text, StringComparison.Ordinal);
    }

    [Fact]
    public void StaticSnapshot_DoesNotHideMalformedFontFaceRule()
    {
        byte[] source = Bytes(
            "<svg xmlns='http://www.w3.org/2000/svg'><style>@font-face{font-family:xkcd</style></svg>");

        byte[] snapshot = SvgStaticSnapshot.Create(source, CancellationToken.None);
        SvgResourceBudgetResult result = SvgResourceBudget.Validate(snapshot, CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal("active-content", result.Reason);
    }

    [Fact]
    public void StaticSnapshot_ObservesCancellation()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => SvgStaticSnapshot.Create(
            Bytes("<svg><animate attributeName='opacity' from='0'/></svg>"),
            cancellation.Token));
    }

    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
}

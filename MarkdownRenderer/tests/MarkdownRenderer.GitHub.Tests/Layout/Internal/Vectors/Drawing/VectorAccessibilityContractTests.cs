using MarkdownRenderer.Accessibility.Internal.Semantics;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Extensions.Vectors;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.RenderResources;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Layout.Internal.Vectors;
using MarkdownRenderer.Layout.Internal.Vectors.Drawing;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas.Geometry;
using System.Globalization;
using Windows.Foundation;
using Windows.UI;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.TestSupport.Assertions.VectorAccessibilityAssertions;
using static MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures.VectorAccessibilityFixture;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.Vectors.Drawing;

/// <summary>
/// Checks the accessible presentation of vector scenes: the semantic
/// palette in normal and High Contrast themes, linked paint roles, host
/// font roles, text-scale tracking, non-scaling strokes, exact math
/// metrics and the captured layout language.
/// </summary>
public sealed class VectorAccessibilityContractTests
{
    [Fact]
    public void InlineForegroundOverrideDoesNotChangeThePaletteOrItsOtherSemanticRoles()
    {
        Color black = Color.FromArgb(255, 0, 0, 0);
        Color white = Color.FromArgb(255, 255, 255, 255);
        Color blue = Color.FromArgb(255, 0, 0, 255);
        Color red = Color.FromArgb(255, 255, 0, 0);
        var original = new VectorScenePaintPalette(
            Foreground: black, Surface: white, Link: blue);
        VectorScenePaintPalette overridden = original with { Foreground = red };

        Assert.Equal(black, original.Resolve(MarkdownVectorPaintRole.Foreground));
        Assert.Equal(red, overridden.Resolve(MarkdownVectorPaintRole.Foreground));
        Assert.Equal(white, overridden.Resolve(MarkdownVectorPaintRole.Surface));
        Assert.Equal(blue, overridden.Resolve(MarkdownVectorPaintRole.Link));
    }

    [Theory]
    [InlineData(16f, 1f)]
    [InlineData(24f, 1.5f)]
    [InlineData(32f, 2f)]
    public void BlockVectorScaleTracksResolvedFontAtCommonTextScales(
        float resolvedFontSize,
        float expectedScale)
    {
        var scene = new MarkdownVectorScene(
            width: 200,
            height: 60,
            baseline: 48,
            commands: Array.Empty<MarkdownVectorCommand>(),
            referenceFontSize: 16);

        Size measured = MeasureBlockScene(scene, resolvedFontSize, resolvedFontSize / 16f);
        Assert.Equal(200 * expectedScale, measured.Width, precision: 4);
        Assert.Equal(60 * expectedScale, measured.Height, precision: 4);
    }

    [Fact]
    public void BlockVectorScaleInfersRichTextReferenceAndFallsBackToTextScale()
    {
        var textScene = new MarkdownVectorScene(
            width: 200,
            height: 60,
            baseline: 48,
            commands:
            [
                MarkdownVectorCommand.DrawText(
                    "Diagram label",
                    new MarkdownVectorPoint(10, 32),
                    new MarkdownVectorTextStyle("Segoe UI", 20),
                    new MarkdownVectorPaintStyle(fillArgb: 0xFF111111)),
            ]);
        var fixedScene = new MarkdownVectorScene(
            width: 200,
            height: 60,
            baseline: 48,
            commands: Array.Empty<MarkdownVectorCommand>());

        Size inferredTextSize = MeasureBlockScene(textScene, resolvedStyleFontSize: 30, textScaleFactor: 2);
        Size textScaleFallbackSize = MeasureBlockScene(fixedScene, resolvedStyleFontSize: 30, textScaleFactor: 2);
        Assert.Equal(300, inferredTextSize.Width, precision: 4);
        Assert.Equal(90, inferredTextSize.Height, precision: 4);
        Assert.Equal(400, textScaleFallbackSize.Width, precision: 4);
        Assert.Equal(120, textScaleFallbackSize.Height, precision: 4);
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(0.25f, 0.5f)]
    public void ZeroSemanticMathKeepsExactIntrinsicMetricsAndSemanticText(float width, float height)
    {
        var scene = new MarkdownVectorScene(
            width,
            height,
            baseline: height,
            commands: Array.Empty<MarkdownVectorCommand>(),
            referenceFontSize: 16,
            viewport: new MarkdownVectorRectangle(0, 0, width, height),
            semantics: Array.Empty<MarkdownVectorSemanticItem>(),
            links: Array.Empty<MarkdownVectorLinkAction>());
        var contentBuilder = new MarkdownContentBuilder();
        contentBuilder.AddVectorScene(
            scene,
            new SourceSpan(0, 1),
            MarkdownStyleRole.Math,
            MarkdownAccessibilityRole.Math,
            accessibilityName: "x",
            semanticText: "x");
        MarkdownContent content = Assert.Single(contentBuilder.Build().Items);
        MarkdownLayoutContext context = CreateContext();
        context.UseProgressiveNativeSceneResidency = true;
        var vector = new VectorSceneBox(context, content, MarkdownElementKeys.Math)
        {
            BlockIndex = 0,
        };

        Assert.False(vector.IsNativeResourcesPrepared);
        float expectedScale = vector.ResolvedStyle.FontSize / 16f;
        vector.Measure(160);
        Assert.Equal(System.Math.Max(1, width * expectedScale), (float)vector.ContentBounds.Width, precision: 4);
        Assert.Equal(System.Math.Max(1, height * expectedScale), (float)vector.ContentBounds.Height, precision: 4);
        Assert.Same(vector.ResolvedStyle, vector.ResolveSemanticTextStyle(-1));

        using var snapshot = new LayoutSnapshot(
            new BlockBox[] { vector },
            context.SourceMap,
            width: 160,
            height: (float)vector.Bounds.Height);
        MarkdownSemanticNode math = Assert.Single(snapshot.SemanticDocument.Root.Children);
        Assert.Equal(MarkdownSemanticRole.Math, math.Role);
        Assert.Equal(MarkdownAccessibilityRole.Math, math.AccessibilityRole);
        Assert.Equal("x", snapshot.SemanticDocument.Text);
    }

    [Fact]
    public void HostFontRoleAppliesDiagramFontFamilyWithoutChangingAuthoredText()
    {
        var hostText = new MarkdownVectorTextStyle(
            "Scene Fallback",
            16,
            fontRole: MarkdownVectorFontRole.Host);
        var authoredText = new MarkdownVectorTextStyle("Authored Face", 16);

        Assert.Equal(
            "Host Diagram Face",
            VectorScenePaintResolver.ResolveFontFamily(hostText, " Host Diagram Face "));
        Assert.Equal(
            "Scene Fallback",
            VectorScenePaintResolver.ResolveFontFamily(hostText, null));
        Assert.Equal(
            "Authored Face",
            VectorScenePaintResolver.ResolveFontFamily(authoredText, "Host Diagram Face"));
        // This test host has no package identity, so the face name stands in
        // for the packaged font file (CanvasFontFamilyReferenceTests).
        Assert.Equal(
            "JetBrains Mono",
            VectorScenePaintResolver.ResolveFontFamily(
                hostText,
                "ms-appx:///Assets/Fonts/JetBrainsMono-Variable.ttf#JetBrains Mono"));
    }

    [Fact]
    public void SemanticPaletteKeepsSurfaceForegroundAndLinkDistinctInHighContrast()
    {
        var palette = new VectorScenePaintPalette(
            Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
            Color.FromArgb(0xFF, 0x00, 0x00, 0x00),
            Color.FromArgb(0xFF, 0x00, 0xFF, 0xFF));

        Color foreground = VectorScenePaintResolver.ResolveColor(
            0x66333333,
            opacity: 0.25f,
            palette,
            MarkdownVectorPaintRole.Foreground,
            highContrast: true);
        Color surface = VectorScenePaintResolver.ResolveColor(
            0x66EEEEEE,
            opacity: 0.25f,
            palette,
            MarkdownVectorPaintRole.Surface,
            highContrast: true);
        Color link = VectorScenePaintResolver.ResolveColor(
            0x660000FF,
            opacity: 0.25f,
            palette,
            MarkdownVectorPaintRole.Link,
            highContrast: true);

        Assert.Equal(palette.Foreground, foreground);
        Assert.Equal(palette.Surface, surface);
        Assert.Equal(palette.Link, link);
        Assert.Equal(0xFF, foreground.A);
        Assert.Equal(0xFF, surface.A);
        Assert.Equal(0xFF, link.A);
    }

    [Theory]
    [InlineData(false, 0xFF0055CCu, 0xFF202020u)]
    [InlineData(true, 0xFF00FFFFu, 0xFFFFFFFFu)]
    public void VectorSemanticTextStyleUsesTheSameLinkedPaintRoleAsDrawing(
        bool highContrast,
        uint expectedLinkedArgb,
        uint expectedPlainArgb)
    {
        MarkdownVectorPaintStyle sharedPaint = MarkdownVectorPaintStyle.CreateSemantic(
            MarkdownVectorPaintRole.Foreground,
            MarkdownVectorPaintRole.Authored);
        MarkdownVectorScene scene = CreateLinkedTextStyleScene(sharedPaint, out MarkdownVectorCommand[] commands);
        MarkdownLayoutContext context = CreateContext(isHighContrast: highContrast);
        var vector = new VectorSceneBox(
            context,
            CreateContent(scene),
            MarkdownElementKeys.Diagram);
        try
        {
            ElementStyle linked = vector.ResolveSemanticTextStyle(1);
            ElementStyle plain = vector.ResolveSemanticTextStyle(2);

            Assert.Equal(ToColor(expectedLinkedArgb), linked.Foreground);
            Assert.Equal(ToColor(expectedPlainArgb), plain.Foreground);
            Assert.Equal("Host Diagram Face", linked.FontFamily);
            Assert.Equal(18, linked.FontSize, precision: 3);
            Assert.Equal((ushort)650, linked.FontWeight.Weight);
            Assert.Equal(Windows.UI.Text.FontStyle.Italic, linked.FontStyle);
            Assert.Equal("Authored face", plain.FontFamily);
            Assert.Equal(14, plain.FontSize, precision: 3);
            Assert.Equal((ushort)350, plain.FontWeight.Weight);

            // Projection is view-time only: the shared immutable scene command and
            // its declared role remain unchanged after both semantic lookups.
            Assert.Same(sharedPaint, commands[0].Style);
            Assert.Same(sharedPaint, commands[1].Style);
            Assert.Equal(MarkdownVectorPaintRole.Foreground, sharedPaint.FillRole);
        }
        finally
        {
            vector.Dispose();
        }
    }

    [Theory]
    [InlineData(false, CanvasStrokeTransformBehavior.Normal)]
    [InlineData(true, CanvasStrokeTransformBehavior.Fixed)]
    public void NonScalingStrokeMapsToWin2DFixedTransformBehavior(
        bool nonScalingStroke,
        CanvasStrokeTransformBehavior expected)
    {
        var style = new MarkdownVectorPaintStyle(
            strokeArgb: 0xFF202020,
            strokeWidth: 2,
            nonScalingStroke: nonScalingStroke);

        using CanvasStrokeStyle strokeStyle = VectorSceneNativeFactory.CreateStrokeStyle(style, new NativeResourceRetirement(createTimer: false));

        Assert.Equal(nonScalingStroke, VectorSceneResourceShapes.RequiresStrokeStyle(style));
        Assert.Equal(expected, strokeStyle.TransformBehavior);
    }

    [Fact]
    public void SemanticPaletteAdaptsContainersAtViewTimeButPreservesFillOnlyAccents()
    {
        var light = new VectorScenePaintPalette(
            Color.FromArgb(0xFF, 0x20, 0x20, 0x20),
            Color.FromArgb(0xFF, 0xFA, 0xFA, 0xFA),
            Color.FromArgb(0xFF, 0x00, 0x55, 0xCC));
        var dark = new VectorScenePaintPalette(
            Color.FromArgb(0xFF, 0xEE, 0xEE, 0xEE),
            Color.FromArgb(0xFF, 0x18, 0x18, 0x18),
            Color.FromArgb(0xFF, 0x66, 0xAA, 0xFF));
        var authoredRectangle = MarkdownVectorCommand.DrawRectangle(
            new MarkdownVectorRectangle(0, 0, 20, 20),
            new MarkdownVectorPaintStyle(fillArgb: 0xFFFFFFFF));
        var semanticRectangle = MarkdownVectorCommand.DrawRectangle(
            new MarkdownVectorRectangle(0, 0, 20, 20),
            MarkdownVectorPaintStyle.CreateSemantic(
                MarkdownVectorPaintRole.Surface,
                MarkdownVectorPaintRole.Authored,
                fillArgb: 0xFFFFFFFF));
        var accentPath = MarkdownVectorCommand.DrawPath(
            [
                MarkdownVectorPathOperation.MoveTo(new MarkdownVectorPoint(0, 0)),
                MarkdownVectorPathOperation.LineTo(new MarkdownVectorPoint(10, 5)),
                MarkdownVectorPathOperation.LineTo(new MarkdownVectorPoint(0, 10)),
                MarkdownVectorPathOperation.Close(),
            ],
            new MarkdownVectorPaintStyle(fillArgb: 0xFFFF5500),
            isClosed: true);

        Assert.Equal(
            MarkdownVectorPaintRole.Surface,
            VectorScenePaintResolver.InferFillRole(authoredRectangle, authoredRectangle.Style!));
        Assert.Equal(
            MarkdownVectorPaintRole.Foreground,
            VectorScenePaintResolver.InferFillRole(accentPath, accentPath.Style!));
        Assert.Equal(
            MarkdownVectorPaintRole.Surface,
            VectorScenePaintResolver.InferFillRole(
                accentPath,
                accentPath.Style!,
                MarkdownVectorSemanticRole.Node));

        Color lightSurface = ResolveFill(semanticRectangle, light);
        Color darkSurface = ResolveFill(semanticRectangle, dark);
        Color authoredLight = ResolveFill(authoredRectangle, light);
        Color authoredDark = ResolveFill(authoredRectangle, dark);
        Color lightAccent = ResolveFill(accentPath, light);
        Color darkAccent = ResolveFill(accentPath, dark);

        Assert.Equal(light.Surface, lightSurface);
        Assert.Equal(dark.Surface, darkSurface);
        Assert.NotEqual(lightSurface, darkSurface);
        Assert.Equal(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), authoredLight);
        Assert.Equal(authoredLight, authoredDark);
        Assert.Equal(lightAccent, darkAccent);
        Assert.Equal(Color.FromArgb(0xFF, 0xFF, 0x55, 0x00), lightAccent);
    }

    [Fact]
    public void LayoutLocalizationUsesTheCapturedRendererLanguage()
    {
        var provider = new RecordingStringProvider("{0:N1}");
        MarkdownLayoutContext context = CreateContext("tr-TR", provider);

        string value = context.ResolveFormattedString("Number", "{0:N1}", 1234.5);

        Assert.Equal("tr-TR", context.Culture.Name);
        Assert.Equal("tr-TR", provider.LastCulture?.Name);
        Assert.Equal(1234.5.ToString("N1", context.Culture), value);
    }

    private static Size MeasureBlockScene(
        MarkdownVectorScene scene,
        float resolvedStyleFontSize,
        double textScaleFactor)
    {
        float baseFontSize = textScaleFactor > 0 && double.IsFinite(textScaleFactor)
            ? (float)(resolvedStyleFontSize / textScaleFactor)
            : resolvedStyleFontSize;
        MarkdownLayoutContext context = CreateContext(
            fontSize: baseFontSize,
            textScaleFactor: textScaleFactor);
        context.UseProgressiveNativeSceneResidency = true;
        var vector = new VectorSceneBox(context, CreateContent(scene), MarkdownElementKeys.Diagram);
        try
        {
            vector.Measure(1024);
            return new Size(vector.ContentBounds.Width, vector.ContentBounds.Height);
        }
        finally
        {
            vector.Dispose();
        }
    }

    private sealed class RecordingStringProvider(string value) : IMarkdownStringProvider
    {
        internal CultureInfo? LastCulture { get; private set; }

        public string? GetString(string key, CultureInfo culture)
        {
            LastCulture = culture;
            return value;
        }
    }
}

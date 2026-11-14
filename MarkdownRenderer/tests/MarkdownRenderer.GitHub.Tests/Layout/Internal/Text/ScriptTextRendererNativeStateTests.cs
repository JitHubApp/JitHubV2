using Microsoft.Graphics.Canvas;
using Xunit;
using MarkdownRenderer.Layout.Internal.Text;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.Text;

/// <summary>
/// Checks the native state of the script text renderer: argument checks
/// before native allocation, color glyph context release, DirectWrite ABI
/// layouts and legacy color renderer flag bounds.
/// </summary>
public sealed class ScriptTextRendererNativeStateTests
{
    [Fact]
    public void ScriptTextRendererRejectsNullForegroundBeforeCreatingNativeState()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        using var target = new CanvasRenderTarget(device, 64, 64, 96f);
        using CanvasDrawingSession drawing = target.CreateDrawingSession();
        var geometry = new ScriptBaselineGeometry.Builder().Build();

        Assert.Throws<ArgumentNullException>(() => new ScriptBaselineTextRenderer(
            drawing,
            geometry,
            defaultForeground: null!));
    }

    [Fact]
    public void NativeColorGlyphContextIsReleasedWhenScriptRendererIsDisposed()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        using var target = new CanvasRenderTarget(device, 64, 64, 96f);
        using (CanvasDrawingSession drawing = target.CreateDrawingSession())
        {
            var renderer = new ScriptBaselineNativeColorGlyphRenderer(drawing);
            renderer.Dispose();
            Assert.False(renderer.IsSupported);
            renderer.Dispose();

            var legacyRenderer = new ScriptBaselineNativeColorGlyphRenderer(
                drawing,
                forceLegacyColorSupport: true);
            Assert.True(legacyRenderer.IsLegacyPath);
            legacyRenderer.Dispose();
            Assert.False(legacyRenderer.IsSupported);
            legacyRenderer.Dispose();
        }
    }

    [Fact]
    public void NativeGlyphRunAndOffsetLayoutsMatchDirectWriteAbi()
    {
        DirectWriteAbiLayoutAssertions.AssertVtableSlotsAndGlyphOffsetLayout();
        int pointerSize = IntPtr.Size;
        DirectWriteAbiLayoutAssertions.AssertGlyphRunLayout(pointerSize);
        DirectWriteAbiLayoutAssertions.AssertColorGlyphRunLayout(pointerSize);
    }

    [Fact]
    public void LegacyColorRendererAcceptsOnlyBoundedOutlineFormatFlags()
    {
        foreach (int format in new[] { 0x1, 0x2, 0x4, 0x3, 0x5, 0x6, 0x7 })
            Assert.True(ScriptBaselineNativeColorGlyphRenderer.IsSupportedOutlineImageFormat(format));

        foreach (int format in new[] { 0, 0x8, 0x10, 0x80, 0x100, 0x105, -1 })
            Assert.False(ScriptBaselineNativeColorGlyphRenderer.IsSupportedOutlineImageFormat(format));
    }
}

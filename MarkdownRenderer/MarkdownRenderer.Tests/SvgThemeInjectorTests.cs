using System.Text;
using MarkdownRenderer.Layout.Boxes;
using Xunit;

namespace MarkdownRenderer.Tests;

public class SvgThemeInjectorTests
{
    [Fact]
    public void Inject_AddsColorAttribute_WhenAbsent()
    {
        var bytes = Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'><path fill='currentColor'/></svg>");
        var result = SvgThemeInjector.Inject(bytes, 0xFF, 0x80, 0x40);
        var s = Encoding.UTF8.GetString(result);
        Assert.Contains("color=\"#FF8040\"", s);
        // Original content preserved.
        Assert.Contains("currentColor", s);
        Assert.Contains("xmlns=", s);
    }

    [Fact]
    public void Inject_PreservesExistingColorAttribute()
    {
        var bytes = Encoding.UTF8.GetBytes("<svg color=\"#000000\" xmlns='http://www.w3.org/2000/svg'/>");
        var result = SvgThemeInjector.Inject(bytes, 0xFF, 0xFF, 0xFF);
        Assert.Same(bytes, result); // unchanged
    }

    [Fact]
    public void Inject_HandlesAttributeBeforeSvgInsensitively()
    {
        // Stroke-color must NOT be treated as the color attribute.
        var bytes = Encoding.UTF8.GetBytes("<svg stroke-color='red' xmlns='x'><g/></svg>");
        var result = SvgThemeInjector.Inject(bytes, 0x10, 0x20, 0x30);
        var s = Encoding.UTF8.GetString(result);
        Assert.Contains("color=\"#102030\"", s);
        Assert.Contains("stroke-color='red'", s);
    }

    [Fact]
    public void Inject_NoSvgRoot_ReturnsOriginal()
    {
        var bytes = Encoding.UTF8.GetBytes("<html/>");
        var result = SvgThemeInjector.Inject(bytes, 1, 2, 3);
        Assert.Same(bytes, result);
    }

    [Fact]
    public void Inject_EmptyBytes_ReturnsOriginal()
    {
        var bytes = System.Array.Empty<byte>();
        var result = SvgThemeInjector.Inject(bytes, 1, 2, 3);
        Assert.Same(bytes, result);
    }

    [Fact]
    public void Inject_PreservesNamespaceAndAttributes()
    {
        var bytes = Encoding.UTF8.GetBytes("<svg width=\"24\" height=\"24\" viewBox=\"0 0 24 24\"><path/></svg>");
        var result = SvgThemeInjector.Inject(bytes, 0xAB, 0xCD, 0xEF);
        var s = Encoding.UTF8.GetString(result);
        Assert.Contains("color=\"#ABCDEF\"", s);
        Assert.Contains("width=\"24\"", s);
        Assert.Contains("height=\"24\"", s);
        Assert.Contains("viewBox=\"0 0 24 24\"", s);
    }

    [Fact]
    public void Inject_SkipsXmlComment_BeforeSvg()
    {
        // A literal "<svg" inside an XML comment must not be mistaken for
        // the root element.
        var bytes = Encoding.UTF8.GetBytes("<!-- example: <svg fake='x'/> --><svg xmlns='http://www.w3.org/2000/svg'><path/></svg>");
        var result = SvgThemeInjector.Inject(bytes, 0x11, 0x22, 0x33);
        var s = Encoding.UTF8.GetString(result);
        Assert.Contains("color=\"#112233\"", s);
        // The injected color attribute must land on the real <svg, not the
        // commented one — the comment text should be untouched.
        Assert.Contains("<!-- example: <svg fake='x'/> -->", s);
        // Exactly one occurrence of the color attribute we added.
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(s, "color=\"#112233\"").Count);
    }

    [Fact]
    public void Inject_SkipsXmlDeclaration_BeforeSvg()
    {
        var bytes = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><svg><path/></svg>");
        var result = SvgThemeInjector.Inject(bytes, 0x10, 0x20, 0x30);
        var s = Encoding.UTF8.GetString(result);
        Assert.Contains("color=\"#102030\"", s);
        Assert.StartsWith("<?xml", s);
    }

    [Fact]
    public void Inject_DoesNotFalsePositive_OnColorInsideAttributeValue()
    {
        // "color=" appearing inside a style="..." attribute value must not
        // be treated as a real color attribute. The injector should still
        // add color="#RRGGBB".
        var bytes = Encoding.UTF8.GetBytes("<svg style=\"background-color: red\"><path/></svg>");
        var result = SvgThemeInjector.Inject(bytes, 0x44, 0x55, 0x66);
        var s = Encoding.UTF8.GetString(result);
        Assert.Contains("color=\"#445566\"", s);
        Assert.Contains("background-color: red", s);
    }
}

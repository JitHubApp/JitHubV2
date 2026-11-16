using System;
using MarkdownRenderer.Layout.Internal.Text;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.Text;

public sealed class CanvasFontFamilyReferenceTests
{
    private const string PackagedMonoFont =
        "ms-appx:///Assets/Fonts/JetBrainsMono-Variable.ttf#JetBrains Mono";

    [Theory]
    [InlineData(PackagedMonoFont)]
    [InlineData("ms-appdata:///local/Fonts/Mono.ttf#Mono Face")]
    public void PackagedProcessKeepsTheFamilyThatSelectsTheFaceInsideTheFontFile(string fontFamily)
    {
        // Win2D reads a font file only from "<uri>#<family>"; the URI alone
        // falls back to the default proportional face.
        Assert.Equal(fontFamily, CanvasFontFamilyReference.ForCanvas($"  {fontFamily} ", processHasPackageIdentity: true));
    }

    [Theory]
    [InlineData(PackagedMonoFont, "JetBrains Mono")]
    [InlineData("ms-appdata:///local/Fonts/Mono.ttf# Mono Face ", "Mono Face")]
    public void UnpackagedProcessUsesTheFamilyNameBecausePackageUrisCannotLoad(string fontFamily, string expected)
    {
        Assert.Equal(expected, CanvasFontFamilyReference.ForCanvas(fontFamily, processHasPackageIdentity: false));
    }

    [Theory]
    [InlineData("Cascadia Mono")]
    [InlineData("Segoe UI, Segoe UI Emoji")]
    [InlineData("file:///C:/Fonts/Mono.ttf#Mono Face")]
    [InlineData("ms-appx:///Assets/Fonts/JetBrainsMono-Variable.ttf")]
    [InlineData("ms-appx:///Assets/Fonts/JetBrainsMono-Variable.ttf#")]
    public void OtherFamiliesPassThroughInEitherProcess(string fontFamily)
    {
        Assert.Equal(fontFamily, CanvasFontFamilyReference.ForCanvas(fontFamily, processHasPackageIdentity: true));
        Assert.Equal(fontFamily, CanvasFontFamilyReference.ForCanvas(fontFamily, processHasPackageIdentity: false));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankFamilyIsRejected(string fontFamily)
    {
        Assert.Throws<ArgumentException>(() => CanvasFontFamilyReference.ForCanvas(fontFamily, processHasPackageIdentity: true));
    }
}

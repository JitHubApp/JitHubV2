using MarkdownRenderer.Theming;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class MarkdownEnvironmentSnapshotTests
{
    [Fact]
    public void PaletteChange_IsBrushOnly()
    {
        var previous = CreateSnapshot();
        var next = previous with { AccentColor = 0xFF0078D4 };

        var changes = next.ChangesFrom(previous);

        Assert.Equal(MarkdownEnvironmentChange.Brushes, changes);
        Assert.True(MarkdownEnvironmentSnapshot.IsBrushOnly(changes));
    }

    [Fact]
    public void HighContrastChange_IsBrushOnly()
    {
        var previous = CreateSnapshot();
        var next = previous with { IsHighContrast = true };

        var changes = next.ChangesFrom(previous);

        Assert.Equal(MarkdownEnvironmentChange.Brushes, changes);
        Assert.True(MarkdownEnvironmentSnapshot.IsBrushOnly(changes));
    }

    [Fact]
    public void TextScaleChange_RequiresTypographyRelayout()
    {
        var previous = CreateSnapshot();
        var changes = (previous with { TextScaleFactor = 2.25 }).ChangesFrom(previous);

        Assert.True(changes.HasFlag(MarkdownEnvironmentChange.TextScale));
        Assert.True(changes.HasFlag(MarkdownEnvironmentChange.Typography));
        Assert.True((changes & MarkdownEnvironmentChange.Relayout) != 0);
        Assert.False(MarkdownEnvironmentSnapshot.IsBrushOnly(changes));
    }

    [Theory]
    [InlineData("dpi")]
    [InlineData("language")]
    [InlineData("flow")]
    [InlineData("width")]
    public void GeometryInputs_RequireRelayout(string input)
    {
        var previous = CreateSnapshot();
        var next = input switch
        {
            "dpi" => previous with { RasterizationScale = 1.5 },
            "language" => previous with { Language = "ar-SA" },
            "flow" => previous with { FlowDirection = 1 },
            "width" => previous with { Width = 900 },
            _ => previous,
        };

        Assert.True((next.ChangesFrom(previous) & MarkdownEnvironmentChange.Relayout) != 0);
    }

    [Fact]
    public void RevisionOnlyChange_DoesNotInvalidateRendering()
    {
        var previous = CreateSnapshot();
        var next = previous.WithRevisions(99, 27, 31);

        Assert.Equal(MarkdownEnvironmentChange.None, next.ChangesFrom(previous));
    }

    [Fact]
    public void NoiseWithinTolerances_DoesNotRelayout()
    {
        var previous = CreateSnapshot();
        var next = previous with
        {
            RasterizationScale = previous.RasterizationScale + 0.00001,
            TextScaleFactor = previous.TextScaleFactor + 0.00001,
            Width = previous.Width + 0.25,
            Language = "EN-us",
        };

        Assert.Equal(MarkdownEnvironmentChange.None, next.ChangesFrom(previous));
    }

    private static MarkdownEnvironmentSnapshot CreateSnapshot() => new(
        Revision: 1,
        BrushRevision: 1,
        LayoutRevision: 1,
        Theme: 1,
        IsHighContrast: false,
        TextScaleFactor: 1,
        Language: "en-US",
        FlowDirection: 0,
        RasterizationScale: 1,
        Width: 800,
        AccentColor: 0xFF0067C0,
        ForegroundColor: 0xFF000000,
        BackgroundColor: 0xFFFFFFFF);
}

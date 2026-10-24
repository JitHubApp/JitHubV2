using MarkdownRenderer.Selection;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// P3-018 (Phase 3 ranks 208, 234 and 337: short READMEs never reported first
/// viewport ready). The canvas was given the fractional document height, for
/// example 439.32 DIP, and XAML layout rounding made it 439 pixels. The last
/// 0.32 DIP of the document could then never be painted, so it was clipped and
/// the readiness probe waited forever for it. The canvas height now rounds up
/// to whole device pixels.
/// </summary>
public sealed class CanvasPixelGridExtentTests
{
    [Theory]
    [InlineData(439.32, 1.0, 440.0)]
    [InlineData(582.34, 1.0, 583.0)]
    [InlineData(461.15, 1.0, 462.0)]
    [InlineData(439.6, 1.0, 440.0)]
    [InlineData(440.0, 1.0, 440.0)]
    [InlineData(500.0, 1.25, 500.0)]
    [InlineData(439.32, 2.0, 439.5)]
    public void ExtentRoundsUpToTheNextWholeDevicePixel(double extent, double scale, double expected)
    {
        double snapped = SelectionController.SnapExtentUpToPixelGrid(extent, scale);
        Assert.Equal(expected, snapped, 9);
        Assert.True(snapped >= extent - 1e-9, "The canvas must never be shorter than the document.");
    }

    [Fact]
    public void FractionalScaleKeepsTheWholeDocumentOnAWholePixelCount()
    {
        double snapped = SelectionController.SnapExtentUpToPixelGrid(439.32, 1.5);
        Assert.True(snapped >= 439.32);
        Assert.Equal(659.0, snapped * 1.5, 9);
    }

    [Fact]
    public void ExtentAlreadyOnThePixelGridDoesNotGainAPixelFromFloatingError()
    {
        // 439.3333... DIP is exactly 659 pixels at 150%, but the product
        // carries a floating error above the whole pixel.
        double onGrid = 659.0 / 1.5;
        Assert.Equal(659.0, SelectionController.SnapExtentUpToPixelGrid(onGrid, 1.5) * 1.5, 9);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void InvalidScaleFallsBackToOneDipPerPixel(double scale)
        => Assert.Equal(440.0, SelectionController.SnapExtentUpToPixelGrid(439.32, scale), 9);

    [Fact]
    public void CommittedCanvasHeightUsesThePixelGridSnap()
    {
        string control = ReadControlSource();
        int member = control.IndexOf("private void ApplySnapshotSize(Size committedSize)", StringComparison.Ordinal);
        Assert.True(member >= 0);
        int end = control.IndexOf("SetSelectionDragShieldActive", member, StringComparison.Ordinal);
        string body = control[member..end];
        Assert.Contains("SelectionController.SnapExtentUpToPixelGrid(", body, StringComparison.Ordinal);
        Assert.Contains("committedSize.Height", body, StringComparison.Ordinal);
        Assert.DoesNotContain("_canvas.Height = Math.Max(1, committedSize.Height);", body, StringComparison.Ordinal);
    }

    private static string ReadControlSource()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string path = Path.Combine(directory.FullName, "MarkdownRenderer", "MarkdownRenderer", "Controls", "MarkdownRendererControl.cs");
            if (File.Exists(path))
                return File.ReadAllText(path);
        }
        throw new FileNotFoundException("Could not locate MarkdownRendererControl.cs.");
    }
}

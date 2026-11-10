using Xunit;
using MarkdownRenderer.Controls.Internal.Focus.Hosted;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Exercises the hosted-focus buffer's storage contract without constructing
/// WinUI controls or claiming hosted Tab/Shift+Tab UI qualification.
/// </summary>
public sealed class HostedFocusableBufferTests
{
    [Fact]
    public void IndexerRejectsNegativeCountAndSpareCapacitySlots()
    {
        var buffer = new HostedFocusableControlBuffer();

        Assert.Equal(0, buffer.Count);
        AssertIndexerThrowsOutOfRange(buffer, -1);
        AssertIndexerThrowsOutOfRange(buffer, 0);
        AssertIndexerThrowsOutOfRange(buffer, 3);

        buffer.Add(null!);
        Assert.Equal(1, buffer.Count);
        Assert.Null(buffer[0]);

        // The first allocation has four slots; only the one below Count is valid.
        AssertIndexerThrowsOutOfRange(buffer, -1);
        AssertIndexerThrowsOutOfRange(buffer, 1);
        AssertIndexerThrowsOutOfRange(buffer, 3);
    }

    [Fact]
    public void GrowthPreservesCountIndexerAndIndexOfSemantics()
    {
        var buffer = new HostedFocusableControlBuffer();
        Assert.Equal(-1, buffer.IndexOf(null!));

        // Cross the 4- and 8-element capacity boundaries without requiring a
        // native Control instance; these tests concern the backing storage only.
        for (int index = 0; index < 9; index++)
            buffer.Add(null!);

        Assert.Equal(9, buffer.Count);
        Assert.Equal(0, buffer.IndexOf(null!));
        foreach (int index in new[] { 0, 3, 4, 7, 8 })
            Assert.Null(buffer[index]);

        // Capacity is 16 after the ninth item, but index 9 is not a member.
        AssertIndexerThrowsOutOfRange(buffer, 9);
    }

    private static void AssertIndexerThrowsOutOfRange(HostedFocusableControlBuffer buffer, int index)
        => Assert.Throws<ArgumentOutOfRangeException>(() => buffer[index]);
}

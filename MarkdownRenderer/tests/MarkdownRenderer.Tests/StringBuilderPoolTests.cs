using MarkdownRenderer.Utilities;
using Xunit;

namespace MarkdownRenderer.Tests;

public class StringBuilderPoolTests
{
    [Fact]
    public void Rent_AfterReturn_ReusesClearedBuilder()
    {
        var first = StringBuilderPool.Rent();
        first.Append("content");
        Assert.Equal("content", StringBuilderPool.ToStringAndReturn(first));

        var second = StringBuilderPool.Rent();
        Assert.Equal(0, second.Length);
        second.Append("next");
        Assert.Equal("next", StringBuilderPool.ToStringAndReturn(second));
    }
}

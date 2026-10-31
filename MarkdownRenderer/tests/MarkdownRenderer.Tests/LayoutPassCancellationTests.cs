using System.Threading;
using MarkdownRenderer.Layout;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class LayoutPassCancellationTests
{
    [Fact]
    public void Push_OverridesFallbackAndRestoresNestedScopes()
    {
        using var outer = new CancellationTokenSource();
        using var inner = new CancellationTokenSource();

        Assert.Equal(CancellationToken.None, LayoutPassCancellation.GetEffectiveToken(CancellationToken.None));

        using (LayoutPassCancellation.Push(outer.Token))
        {
            Assert.Equal(outer.Token, LayoutPassCancellation.GetEffectiveToken(CancellationToken.None));

            using (LayoutPassCancellation.Push(inner.Token))
            {
                Assert.Equal(inner.Token, LayoutPassCancellation.GetEffectiveToken(CancellationToken.None));
            }

            Assert.Equal(outer.Token, LayoutPassCancellation.GetEffectiveToken(CancellationToken.None));
        }

        Assert.Equal(CancellationToken.None, LayoutPassCancellation.GetEffectiveToken(CancellationToken.None));
    }

    [Fact]
    public void ActivePassCancellation_IsObservedAtNestedCheckpoint()
    {
        using var cts = new CancellationTokenSource();
        using (LayoutPassCancellation.Push(cts.Token))
        {
            cts.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() =>
                NestedCheckpoint(depth: 64, CancellationToken.None));
        }
    }

    private static void NestedCheckpoint(int depth, CancellationToken fallback)
    {
        if (depth == 0)
        {
            LayoutPassCancellation.GetEffectiveToken(fallback).ThrowIfCancellationRequested();
            return;
        }

        NestedCheckpoint(depth - 1, fallback);
    }
}

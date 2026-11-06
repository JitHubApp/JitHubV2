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
        var pass = new LayoutPassCancellation();

        Assert.Equal(CancellationToken.None, pass.GetEffectiveToken(CancellationToken.None));

        using (pass.Push(outer.Token))
        {
            Assert.Equal(outer.Token, pass.GetEffectiveToken(CancellationToken.None));

            using (pass.Push(inner.Token))
            {
                Assert.Equal(inner.Token, pass.GetEffectiveToken(CancellationToken.None));
            }

            Assert.Equal(outer.Token, pass.GetEffectiveToken(CancellationToken.None));
        }

        Assert.Equal(CancellationToken.None, pass.GetEffectiveToken(CancellationToken.None));
    }

    [Fact]
    public void ActivePassCancellation_IsObservedAtNestedCheckpoint()
    {
        using var cts = new CancellationTokenSource();
        var pass = new LayoutPassCancellation();
        using (pass.Push(cts.Token))
        {
            cts.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() =>
                NestedCheckpoint(pass, depth: 64, CancellationToken.None));
        }
    }

    [Fact]
    public void Push_IsVisibleOnlyThroughItsOwnPassState()
    {
        using var cts = new CancellationTokenSource();
        var first = new LayoutPassCancellation();
        var second = new LayoutPassCancellation();

        using (first.Push(cts.Token))
        {
            Assert.Equal(cts.Token, first.GetEffectiveToken(CancellationToken.None));
            Assert.Equal(CancellationToken.None, second.GetEffectiveToken(CancellationToken.None));
        }
    }

    private static void NestedCheckpoint(LayoutPassCancellation pass, int depth, CancellationToken fallback)
    {
        if (depth == 0)
        {
            pass.GetEffectiveToken(fallback).ThrowIfCancellationRequested();
            return;
        }

        NestedCheckpoint(pass, depth - 1, fallback);
    }
}

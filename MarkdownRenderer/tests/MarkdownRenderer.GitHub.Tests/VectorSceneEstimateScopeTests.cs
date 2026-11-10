using System.Collections.Generic;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Performance.Internal.Residency;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Scene weight memoization belongs to the scope that passes the cache in,
/// normally one performance session, rather than to the process.
/// </summary>
public sealed class VectorSceneEstimateScopeTests
{
    [Fact]
    public void PoliciesSharingAScopeWalkASceneOnce()
    {
        MarkdownVectorScene scene = CreateScene();
        var estimates = new MarkdownVectorSceneEstimateCache();
        var first = new MarkdownVectorSceneResidencyPolicy([], estimates: estimates);
        var second = new MarkdownVectorSceneResidencyPolicy([], estimates: estimates);
        try
        {
            long weight = first.GetPreparedResourceEstimate(scene);

            Assert.Equal(weight, second.GetPreparedResourceEstimate(scene));
            Assert.Equal(1, first.CommandGraphEstimateCount);
            Assert.Equal(0, second.CommandGraphEstimateCount);
        }
        finally
        {
            first.Dispose();
            second.Dispose();
        }
    }

    [Fact]
    public void SeparateScopesDoNotShareSceneWeights()
    {
        MarkdownVectorScene scene = CreateScene();
        var first = new MarkdownVectorSceneResidencyPolicy([], estimates: new MarkdownVectorSceneEstimateCache());
        var second = new MarkdownVectorSceneResidencyPolicy([]);
        try
        {
            long weight = first.GetPreparedResourceEstimate(scene);

            Assert.Equal(weight, second.GetPreparedResourceEstimate(scene));
            Assert.Equal(1, first.CommandGraphEstimateCount);
            Assert.Equal(1, second.CommandGraphEstimateCount);
        }
        finally
        {
            first.Dispose();
            second.Dispose();
        }
    }

    private static MarkdownVectorScene CreateScene()
    {
        var operations = new List<MarkdownVectorPathOperation>
        {
            MarkdownVectorPathOperation.MoveTo(new MarkdownVectorPoint(0, 0)),
            MarkdownVectorPathOperation.LineTo(new MarkdownVectorPoint(8, 8)),
            MarkdownVectorPathOperation.Close(),
        };
        return new MarkdownVectorScene(
            width: 8,
            height: 8,
            baseline: 8,
            commands: [MarkdownVectorCommand.FillPath(operations)]);
    }
}

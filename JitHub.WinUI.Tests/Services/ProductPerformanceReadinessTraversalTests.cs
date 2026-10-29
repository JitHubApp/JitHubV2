using System.Collections.Concurrent;
using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

/// <summary>
/// Tests that turn on <c>JITHUB_PERFORMANCE_FIXTURE</c> and drive the static
/// <see cref="ProductPerformanceReadiness"/> traversal state. Both are process-wide:
/// while the variable is set, product code under test in other classes (for example
/// <c>RepoCodePageViewModel.SelectFileAsync</c>) records traversal stages into the same
/// static traversal, and the variable changes <see cref="ProductPerformanceReadiness.IsEnabled"/>
/// for every test in the process. xUnit runs this collection on its own, after the
/// parallel collections finish.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProductPerformanceReadinessCollection
{
    public const string Name = "Product performance readiness (process-wide)";
}

[Collection(ProductPerformanceReadinessCollection.Name)]
public sealed class ProductPerformanceReadinessTraversalTests
{
    private const string FixtureVariable = "JITHUB_PERFORMANCE_FIXTURE";

    [Fact]
    public void TraversalCommitEndsGenerationAndRejectsLaterStages()
    {
        string? previous = Environment.GetEnvironmentVariable(FixtureVariable);
        // The static event may be raised from any thread, so collect stages thread-safely
        // and assert on one snapshot.
        ConcurrentQueue<ProductPerformanceTraversalStage> stages = new();
        EventHandler<ProductPerformanceTraversalStage> handler = (_, stage) => stages.Enqueue(stage);
        ProductPerformanceReadiness.TraversalStageRecorded += handler;
        try
        {
            Environment.SetEnvironmentVariable(FixtureVariable, "1");
            ProductPerformanceReadiness.BeginTraversal("stars", "repo:42", "repo_code");
            ProductPerformanceReadiness.RecordTraversalStage("before.commit");
            ProductPerformanceReadiness.CommitTraversal("repo_code", "repo:42");
            ProductPerformanceReadiness.RecordTraversalStage("after.commit");

            ProductPerformanceTraversalStage stage = Assert.Single(
                stages.ToArray(),
                candidate => candidate.Stage is "before.commit" or "after.commit");
            Assert.Equal("before.commit", stage.Stage);
            Assert.True(stage.Generation > 0);
        }
        finally
        {
            ProductPerformanceReadiness.CancelTraversal();
            ProductPerformanceReadiness.TraversalStageRecorded -= handler;
            Environment.SetEnvironmentVariable(FixtureVariable, previous);
        }
    }

    [Fact]
    public void TraversalMeasurement_ResumesProbeAfterCommitOrCancellation()
    {
        string? previous = Environment.GetEnvironmentVariable(FixtureVariable);
        int armed = 0;
        int completed = 0;
        EventHandler armedHandler = (_, _) => Interlocked.Increment(ref armed);
        EventHandler completedHandler = (_, _) => Interlocked.Increment(ref completed);
        ProductPerformanceReadiness.TraversalMeasurementArmed += armedHandler;
        ProductPerformanceReadiness.TraversalMeasurementCompleted += completedHandler;
        try
        {
            Environment.SetEnvironmentVariable(FixtureVariable, "1");

            ProductPerformanceReadiness.ArmTraversalMeasurement();
            ProductPerformanceReadiness.BeginTraversal("repo_code", "file:1", "repo_code");
            ProductPerformanceReadiness.CommitTraversal("repo_code", "file:1");

            ProductPerformanceReadiness.ArmTraversalMeasurement();
            ProductPerformanceReadiness.BeginTraversal("repo_code", "file:2", "repo_code");
            ProductPerformanceReadiness.CancelTraversal();

            Assert.Equal(2, Volatile.Read(ref armed));
            Assert.Equal(2, Volatile.Read(ref completed));
        }
        finally
        {
            ProductPerformanceReadiness.CancelTraversal();
            ProductPerformanceReadiness.TraversalMeasurementArmed -= armedHandler;
            ProductPerformanceReadiness.TraversalMeasurementCompleted -= completedHandler;
            Environment.SetEnvironmentVariable(FixtureVariable, previous);
        }
    }
}

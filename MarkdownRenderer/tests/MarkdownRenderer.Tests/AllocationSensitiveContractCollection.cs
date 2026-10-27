using Xunit;

namespace MarkdownRenderer.Tests;

// Per-thread allocation counters must be measured away from concurrently
// executing stress fixtures. This does not change warm-up, iteration counts,
// allocation limits, or establish the cause of a previous non-zero result.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AllocationSensitiveContractCollection
{
    public const string Name = "Allocation-sensitive contracts";
}

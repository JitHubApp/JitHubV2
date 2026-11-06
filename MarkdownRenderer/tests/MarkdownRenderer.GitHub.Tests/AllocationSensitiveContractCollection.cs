using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

// Keep allocation-counter measurements apart from parallel rendering fixtures.
// Warm-up, iteration counts, zero-byte limits and test selection are unchanged;
// serialization alone does not explain historical allocation failures.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AllocationSensitiveContractCollection
{
    public const string Name = "Allocation-sensitive contracts";
}

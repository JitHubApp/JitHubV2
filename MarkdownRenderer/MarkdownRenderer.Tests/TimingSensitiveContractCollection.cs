using Xunit;

namespace MarkdownRenderer.Tests;

// These contracts exercise real wall-clock cancellation and SVG security
// deadlines. Running them beside CPU-heavy stress fixtures can make a tiny
// valid input miss the production deadline without testing either contract.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimingSensitiveContractCollection
{
    public const string Name = "Timing-sensitive contracts";
}

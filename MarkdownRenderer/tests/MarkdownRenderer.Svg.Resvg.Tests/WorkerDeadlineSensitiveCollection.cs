using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

// Provider tests measure real worker requests against the product's hard
// request deadline, and the SVG resource-budget preflight runs against its
// wall-clock security deadline. Run them apart from the other worker-spawning
// test classes so CPU contention on small CI runners does not count against
// those deadlines. The deadlines, fixtures, and assertions are unchanged.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WorkerDeadlineSensitiveCollection
{
    public const string Name = "Worker deadline-sensitive provider tests";
}

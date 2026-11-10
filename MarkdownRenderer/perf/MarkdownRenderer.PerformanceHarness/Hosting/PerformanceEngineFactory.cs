using MarkdownRenderer.PerformanceHarness.Core.Configuration;

namespace MarkdownRenderer.PerformanceHarness.Hosting;

/// <summary>
/// Builds the GFM-strict engines every measurement uses, with parse limits
/// and a parse-cache budget sized to the measured source.
/// </summary>
internal static class PerformanceEngineFactory
{
    internal static MarkdownEngine Create(
        int sourceBytes,
        long? parseCacheBudgetBytes = null)
        => new MarkdownEngineBuilder()
            .UseProfile(MarkdownProfiles.GfmStrict)
            .WithParseLimits(PerformanceParsePolicy.CreateLimits(sourceBytes))
            .WithParseCacheBudgetBytes(
                parseCacheBudgetBytes ?? Math.Max(0, sourceBytes * 8L))
            .Build();
}

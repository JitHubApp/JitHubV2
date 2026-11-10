using MarkdownRenderer.PerformanceHarness.Core.Reports;
using MarkdownRenderer.PerformanceHarness.Core.Scenarios;

namespace MarkdownRenderer.PerformanceHarness;

/// <summary>
/// The harness's self-check of its own retained-memory results before a
/// release report is written.
/// </summary>
internal static class RetainedMemoryResultCheck
{
    /// <summary>
    /// Adds a failure for each first-viewport source size whose
    /// retained-memory result is missing, duplicated, inconsistent or failing.
    /// </summary>
    internal static void AddFailures(PerformanceReport report)
    {
        if (report.RetainedMemory.Count != MeasuredSourceSizes.FirstViewport.Length)
            report.Failures.Add("Retained-memory report did not contain exactly the three required source sizes.");

        foreach (int sourceBytes in MeasuredSourceSizes.FirstViewport)
        {
            RetainedMemoryResult[] matches = report.RetainedMemory
                .Where(item => item.SourceUtf16Bytes == sourceBytes)
                .ToArray();
            long expectedBudget = RetainedMemoryMeasurement.GetBudgetBytes(sourceBytes);
            bool valid = matches.Length == 1 &&
                         matches[0].Corpus == PerformanceDocumentFactory.GetCorpusName(sourceBytes) &&
                         matches[0].ManagedDeltaBytes >= 0 &&
                         matches[0].PrivateDeltaBytes >= 0 &&
                         matches[0].BudgetBytes == expectedBudget &&
                         matches[0].ManagedDeltaBytes <= expectedBudget &&
                         matches[0].PrivateDeltaBytes <= expectedBudget &&
                         matches[0].Passed;
            if (!valid)
                report.Failures.Add($"Retained-memory gate failed or lacked evidence for {sourceBytes:N0} UTF-16 bytes.");
        }
    }
}

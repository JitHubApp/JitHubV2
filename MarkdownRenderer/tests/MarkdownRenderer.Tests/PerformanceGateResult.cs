namespace MarkdownRenderer.Tests;

/// <summary>
/// The outcome of one run of the external performance-gate script: whether it
/// exited with success and its combined standard output and error.
/// </summary>
internal readonly record struct PerformanceGateResult(bool Passed, string Output);

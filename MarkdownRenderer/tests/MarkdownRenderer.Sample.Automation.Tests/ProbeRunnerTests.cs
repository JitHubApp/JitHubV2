using MarkdownRenderer.Sample.Automation;
using Xunit;

namespace MarkdownRenderer.Sample.Automation.Tests;

public sealed class ProbeRunnerTests
{
    [Fact]
    public void AllPassingProbesPrintPassesAndExitZero()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var runner = new ProbeRunner(output, error);

        runner.Run("first", () => { });
        runner.Run("second", () => { });
        int exitCode = runner.WriteSummary();

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Equal(
            Lines(
                "[automation] PASS first",
                "[automation] PASS second",
                "",
                "──────────────── results ────────────────",
                "  PASS first",
                "  PASS second",
                "2 passed, 0 failed"),
            output.ToString());
    }

    [Fact]
    public void AFailingProbeIsRecordedWithItsMessageAndFailsTheRun()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var runner = new ProbeRunner(output, error);

        runner.Run("broken", () => throw new InvalidOperationException("renderer missing"));
        runner.Run("after", () => { });
        int exitCode = runner.WriteSummary();

        Assert.Equal(1, exitCode);
        Assert.StartsWith(
            "[automation] FAIL broken: System.InvalidOperationException: renderer missing",
            error.ToString(),
            StringComparison.Ordinal);
        Assert.Equal(
            Lines(
                "[automation] PASS after",
                "",
                "──────────────── results ────────────────",
                "  PASS after",
                "  FAIL broken: renderer missing",
                "1 passed, 1 failed"),
            output.ToString());
    }

    private static string Lines(params string[] lines) =>
        string.Concat(lines.Select(line => line + Environment.NewLine));
}

namespace MarkdownRenderer.Sample.Automation;

internal static class ProcessExitValidation
{
    internal static string? GetFailure(bool hasExited, int? exitCode, bool teardownObserved)
    {
        if (!hasExited)
            return "The sample did not exit before the shutdown deadline.";
        if (exitCode is null)
            return "The process exit code was not captured.";
        if (exitCode != 0)
            return $"The sample exited abnormally: 0x{unchecked((uint)exitCode.Value):X8}.";
        return teardownObserved ? null : "No renderer disposal-completed signal was observed for this process.";
    }
}

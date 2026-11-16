namespace MarkdownRenderer.Sample.Automation.Uia;

/// <summary>
/// Fails the current probe with an actionable message.
/// </summary>
internal static class ProbeAssert
{
    internal static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

using System.Text.Json;

namespace MarkdownRenderer.Sample.Automation;

/// <summary>
/// Reads the JSON the sample writes when a renderer is disposed (the file named
/// by <see cref="SampleAppLauncher.DisposalEvidenceVariable"/>).
/// </summary>
internal static class DisposalEvidence
{
    /// <summary>
    /// Returns whether <paramref name="evidenceJson"/> was written by process
    /// <paramref name="processId"/> after every attached renderer was disposed.
    /// </summary>
    internal static bool ShowsCompletedDisposal(string evidenceJson, int processId)
    {
        using var evidence = JsonDocument.Parse(evidenceJson);
        return evidence.RootElement.GetProperty("processId").GetInt32() == processId &&
            evidence.RootElement.GetProperty("disposalCompleted").GetBoolean();
    }
}

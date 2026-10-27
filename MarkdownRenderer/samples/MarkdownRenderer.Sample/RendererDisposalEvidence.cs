using System;
using System.IO;
using System.Text.Json;
using MarkdownRenderer.Controls;

// The probe observes the common implementation shared by both public view types.
#pragma warning disable MR1001

namespace MarkdownRenderer.Sample;

internal static class RendererDisposalEvidence
{
    internal const string OutputEnvironmentVariable = "MARKDOWN_RENDERER_DISPOSAL_EVIDENCE";
    private static int _attached;
    private static int _disposed;

    internal static void Attach(MarkdownRendererControl view)
    {
        string? path = Environment.GetEnvironmentVariable(OutputEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(path))
            return;
        _attached++;
        view.DisposalCompleted += (_, _) => File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            processId = Environment.ProcessId,
            disposalCompleted = ++_disposed == _attached,
            disposedViews = _disposed,
            attachedViews = _attached,
        }));
    }
}

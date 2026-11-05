using System;
using System.IO;
using System.Text.Json;
using MarkdownRenderer.Controls;

// The probe observes the common implementation shared by both public view types.

namespace MarkdownRenderer.Sample;

/// <summary>
/// Writes, for the UI automation driver, whether every renderer view a window
/// created has been disposed. The window owns one instance and attaches each
/// view it creates; nothing is recorded unless the driver set the output path.
/// </summary>
internal sealed class RendererDisposalEvidence
{
    internal const string OutputEnvironmentVariable = "MARKDOWN_RENDERER_DISPOSAL_EVIDENCE";
    private readonly string? _outputPath;
    private int _attached;
    private int _disposed;

    private RendererDisposalEvidence(string? outputPath) => _outputPath = outputPath;

    internal static RendererDisposalEvidence FromEnvironment()
        => new(Environment.GetEnvironmentVariable(OutputEnvironmentVariable));

    internal void Attach(MarkdownRendererControl view)
    {
        if (string.IsNullOrWhiteSpace(_outputPath))
            return;
        string path = _outputPath;
        _attached++;
        view.Disposed += (_, _) => File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            processId = Environment.ProcessId,
            disposalCompleted = ++_disposed == _attached,
            disposedViews = _disposed,
            attachedViews = _attached,
        }));
    }
}

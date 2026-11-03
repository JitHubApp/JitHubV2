using System.Runtime.InteropServices;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>Starts the packaged resvg worker executable.</summary>
internal sealed class WindowsWorkerProcessLauncher(
    string workerPath,
    ResvgMarkdownSvgRendererOptions options) : IWorkerProcessLauncher
{
    public Task<WindowsWorkerProcess> StartAsync(WorkerJob job, CancellationToken cancellationToken) =>
        WindowsWorkerProcess.StartAsync(workerPath, options, job, cancellationToken);
}

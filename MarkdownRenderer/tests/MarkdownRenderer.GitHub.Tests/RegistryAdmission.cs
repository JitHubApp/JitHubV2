using MarkdownRenderer.Images;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// An SVG admission double for pure owner registry tests: it fails if
/// asked to wait for capacity or to open a document.
/// </summary>
internal sealed class RegistryAdmission : IMarkdownSvgAdmission
{
    public object AdmissionDomain { get; } = new();
    public long CapacityVersion => 0;
    public Task WaitForCapacityAsync(
        long requiredFreeBytes,
        MarkdownSvgRenderPriority priority,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Pure identity tests must not start admission waiters.");
    public ValueTask<MarkdownSvgAdmissionResult> TryOpenAsync(
        MarkdownSvgOpenRequest request, MarkdownSvgRenderDemand demand, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Pure identity tests must not open a document.");
}

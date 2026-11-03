using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// Creates and releases the shared-memory sections lent to a worker for one
/// transaction. Each helper clears its source-bearing arguments before the
/// returned task completes, so a retained task never keeps SVG source alive.
/// </summary>
internal static class WorkerSharedMemoryMapping
{
    internal static SharedMemoryLease CreateSourceMemory(string name, SvgSourceBuffer source) =>
        new(name, source, outputLength: 0);

    internal static async Task<TResult> WithSourceMappingAsync<TResult>(
        Func<string, SvgSourceBuffer, SharedMemoryLease>? sourceMemoryFactory,
        string sourceMemoryName,
        SvgSourceBuffer source,
        Func<SharedMemoryLease, Task<TResult>> operation,
        CancellationToken cancellationToken,
        Action? contentAdmissionGuard = null,
        WorkerObjectSecurity.WorkerObjectSecurityContext? securityContext = null)
    {
        SharedMemoryLease? sourceMemory = null;
        try
        {
            if ((sourceMemoryFactory is null) == (securityContext is null))
                throw new ArgumentException("Exactly one source mapping factory or worker security context is required.");
            ArgumentException.ThrowIfNullOrWhiteSpace(sourceMemoryName);
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(operation);
            cancellationToken.ThrowIfCancellationRequested();
            contentAdmissionGuard?.Invoke();

            sourceMemory = (securityContext is null
                    ? sourceMemoryFactory!(sourceMemoryName, source)
                    : new SharedMemoryLease(
                        sourceMemoryName,
                        source,
                        outputLength: 0,
                        securityContext: securityContext)) ??
                throw new InvalidOperationException("The SVG source-memory factory returned no mapping.");
            cancellationToken.ThrowIfCancellationRequested();
            // Quarantine can be published while the mapping factory runs.
            // Revalidate before the operation can send an OPEN/reattachment.
            contentAdmissionGuard?.Invoke();
            return await operation(sourceMemory).ConfigureAwait(false);
        }
        finally
        {
            // Clear source-bearing parameters before disposing the mapping so
            // even disposal faults cannot leave them on a completed Task.
            SharedMemoryLease? mapping = sourceMemory;
            sourceMemory = null;
            source = SvgSourceBuffer.Empty;
            sourceMemoryFactory = null;
            operation = null!;
            contentAdmissionGuard = null;
            sourceMemoryName = string.Empty;
            if (mapping is not null)
                ((IDisposable)mapping).Dispose();
        }
    }

    internal static async Task<WorkerRenderResult> WithAdmittedOutputMappingAsync<TAdmission>(
        Func<CancellationToken, Task<TAdmission>> admitAsync,
        Func<TAdmission, SharedMemoryLease, CancellationToken, Task<WorkerResponse>> renderAsync,
        Func<string, int, SharedMemoryLease>? outputMemoryFactory,
        string outputMemoryName,
        int outputLength,
        CancellationToken cancellationToken,
        Func<TAdmission, WorkerObjectSecurity.WorkerObjectSecurityContext>? securityContextFactory = null)
        where TAdmission : IDisposable
    {
        TAdmission admission = default!;
        bool hasAdmission = false;
        SharedMemoryLease? outputMemory = null;
        try
        {
            ArgumentNullException.ThrowIfNull(admitAsync);
            ArgumentNullException.ThrowIfNull(renderAsync);
            if ((outputMemoryFactory is null) == (securityContextFactory is null))
                throw new ArgumentException("Exactly one output mapping factory or worker security-context selector is required.");
            ArgumentException.ThrowIfNullOrWhiteSpace(outputMemoryName);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputLength);
            cancellationToken.ThrowIfCancellationRequested();

            admission = await admitAsync(cancellationToken).ConfigureAwait(false);
            hasAdmission = true;
            cancellationToken.ThrowIfCancellationRequested();
            outputMemory = (securityContextFactory is null
                    ? outputMemoryFactory!(outputMemoryName, outputLength)
                    : new SharedMemoryLease(
                        outputMemoryName,
                        outputLength,
                        securityContext: securityContextFactory(admission))) ??
                throw new InvalidOperationException("The SVG output-memory factory returned no mapping.");
            cancellationToken.ThrowIfCancellationRequested();
            WorkerResponse response = await renderAsync(admission, outputMemory, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var result = new WorkerRenderResult(response, outputMemory);
            outputMemory = null;
            return result;
        }
        finally
        {
            try
            {
                if (outputMemory is not null)
                    ((IDisposable)outputMemory).Dispose();
            }
            finally
            {
                try
                {
                    if (hasAdmission)
                        ((IDisposable)admission).Dispose();
                }
                finally
                {
                    // Delegates supplied by RenderDocumentAsync close over
                    // preflight/openRequest and can otherwise keep their
                    // source arrays alive as long as this completed Task.
                    outputMemory = null;
                    admission = default!;
                    admitAsync = null!;
                    renderAsync = null!;
                    outputMemoryFactory = null;
                    outputMemoryName = string.Empty;
                }
            }
        }
    }

    internal static async Task<WorkerResponse?> AttachDocumentIfMissingAsync(
        WorkerResponse response,
        Func<Task<WorkerResponse>> attach)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(attach);
            return response.Status == WorkerStatus.DocumentMissing
                ? await attach().ConfigureAwait(false)
                : null;
        }
        finally
        {
            // The no-reattach path must release the unused source-bearing
            // closure just as reliably as success, failure, and cancellation.
            attach = null!;
        }
    }

    internal static string NewSourceMappingName() =>
        $"Local\\MarkdownRenderer.Resvg.{Environment.ProcessId}.{Guid.NewGuid():N}";

    internal static SharedMemoryLease CreateOutputMemory(string name, int outputLength) =>
        new(name, outputLength);

    internal static string NewOutputMappingName() =>
        $"Local\\MarkdownRenderer.Resvg.Output.{Environment.ProcessId}.{Guid.NewGuid():N}";
}

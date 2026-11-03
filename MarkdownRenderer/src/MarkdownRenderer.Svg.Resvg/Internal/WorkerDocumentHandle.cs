using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Svg.Resvg.Internal;

internal sealed class WorkerDocumentHandle
{
    private long _preferredWorkerInstanceId;
    private Action? _contentAdmissionGuard;
    private Action? _contentFailureCallback;

    public WorkerDocumentHandle(
        ulong documentId,
        long preferredWorkerInstanceId = 0,
        Action? contentAdmissionGuard = null,
        Action? contentFailureCallback = null)
    {
        DocumentId = documentId;
        _preferredWorkerInstanceId = preferredWorkerInstanceId;
        _contentAdmissionGuard = contentAdmissionGuard;
        _contentFailureCallback = contentFailureCallback;
    }

    public ulong DocumentId { get; }

    public long PreferredWorkerInstanceId => Interlocked.Read(ref _preferredWorkerInstanceId);

    public void ValidateContentAdmission() => Volatile.Read(ref _contentAdmissionGuard)?.Invoke();

    public void QuarantineContent() => Interlocked.Exchange(ref _contentFailureCallback, null)?.Invoke();

    public void ReleaseContentAdmissionGuard()
    {
        Interlocked.Exchange(ref _contentAdmissionGuard, null);
        Interlocked.Exchange(ref _contentFailureCallback, null);
    }

    public void PreferWorker(long workerInstanceId)
    {
        if (workerInstanceId > 0)
            Interlocked.Exchange(ref _preferredWorkerInstanceId, workerInstanceId);
    }
}

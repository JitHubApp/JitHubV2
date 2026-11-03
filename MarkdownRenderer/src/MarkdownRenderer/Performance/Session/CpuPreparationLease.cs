using System;
using System.Threading;

namespace MarkdownRenderer.Performance;

/// <summary>Holds one CPU-preparation admission and one unit of session work.</summary>
internal sealed class CpuPreparationLease(
    IDisposable admissionLease,
    MarkdownSessionAdmissions admissions,
    MarkdownPerformanceSessionLifetime lifetime,
    long started) : IDisposable
{
    private IDisposable? _admissionLease = admissionLease;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _admissionLease, null) is not { } held)
            return;
        try
        {
            held.Dispose();
        }
        finally
        {
            admissions.RecordCpuPreparation(started);
            lifetime.RetireWork();
        }
    }
}

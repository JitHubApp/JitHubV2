using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Diagnostics.Internal;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Performance;

/// <summary>
/// Owns the session's fair CPU-preparation, scene-preparation and in-flight
/// source-byte admissions, and the preparation counters reported by
/// <see cref="MarkdownPerformanceSession.GetSnapshot"/>. Every lease it hands
/// out also holds one unit of session work.
/// </summary>
internal sealed class MarkdownSessionAdmissions
{
    private readonly MarkdownPerformanceSessionLifetime _lifetime;
    private readonly FairDocumentAdmission _cpuPreparationAdmission;
    private readonly FairDocumentAdmission _scenePreparationAdmission;
    private long _cpuPreparations;
    private long _cpuPreparationMilliseconds;
    private long _scenePreparations;
    private long _scenePreparationMilliseconds;

    internal MarkdownSessionAdmissions(
        MarkdownPerformanceSessionLifetime lifetime,
        MarkdownPerformanceOptions options)
    {
        _lifetime = lifetime;
        _cpuPreparationAdmission = new FairDocumentAdmission(options.MaxConcurrentCpuPreparations);
        _scenePreparationAdmission = new FairDocumentAdmission(options.MaxConcurrentScenePreparations);
        SourceBytes = new SourceByteAdmission(
            options.MaxInFlightSourceBytes, options.ReservedVisibleSourceBytes);
    }

    internal SourceByteAdmission SourceBytes { get; }

    // Admission queue depth is sampled only by internal diagnostics/tests. It
    // adds no work to the CPU-preparation hot path.
    internal int PendingCpuPreparations => _cpuPreparationAdmission.PendingRequests;

    internal long CpuPreparations => Interlocked.Read(ref _cpuPreparations);
    internal long CpuPreparationMilliseconds => Interlocked.Read(ref _cpuPreparationMilliseconds);
    internal long ScenePreparations => Interlocked.Read(ref _scenePreparations);
    internal long ScenePreparationMilliseconds => Interlocked.Read(ref _scenePreparationMilliseconds);

    internal async ValueTask<IDisposable> EnterCpuPreparationAsync(
        object documentOwner,
        bool exclusive,
        CancellationToken cancellationToken)
    {
        _lifetime.RegisterWork();
        IDisposable? admissionLease = null;
        try
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _lifetime.Token);
            admissionLease = await _cpuPreparationAdmission.EnterAsync(
                documentOwner, exclusive, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            CpuPreparationLease lease = new(
                admissionLease, this, _lifetime, Stopwatch.GetTimestamp());
            admissionLease = null;
            Interlocked.Increment(ref _cpuPreparations);
            return lease;
        }
        catch
        {
            try
            {
                admissionLease?.Dispose();
            }
            finally
            {
                _lifetime.RetireWork();
            }
            throw;
        }
    }

    internal async ValueTask<IDisposable> ReserveSourceBytesAsync(
        object documentOwner,
        long bytes,
        bool background,
        CancellationToken cancellationToken,
        CancellationToken requestCancellationToken)
    {
        ArgumentNullException.ThrowIfNull(documentOwner);
        if (background && bytes > SourceBytes.BackgroundCapacity)
            throw new MarkdownImageSourceDeferredException();

        _lifetime.RegisterWork();
        IDisposable? admissionLease = null;
        CancellationTokenSource? linked = null;
        try
        {
            linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, requestCancellationToken, _lifetime.Token);
            admissionLease = await SourceBytes.EnterAsync(
                    documentOwner, bytes, background, linked.Token)
                .ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();

            var result = new SessionSourceByteLease(_lifetime, admissionLease, linked);
            admissionLease = null;
            linked = null;
            return result;
        }
        catch
        {
            if (admissionLease is not null)
            {
                try { admissionLease.Dispose(); }
                catch (Exception cleanupException)
                {
                    LogLeaseCleanupFailure("unpublished source-byte admission", cleanupException);
                }
            }
            if (linked is not null)
            {
                try { linked.Dispose(); }
                catch (Exception cleanupException)
                {
                    LogLeaseCleanupFailure("unpublished source-byte cancellation", cleanupException);
                }
            }
            _lifetime.RetireWork();
            throw;
        }
    }

    internal async ValueTask<IMarkdownScenePreparationLease> EnterScenePreparationAsync(
        object documentOwner,
        CancellationToken cancellationToken)
    {
        _lifetime.RegisterWork();
        CancellationTokenSource? linked = null;
        IDisposable? admissionLease = null;
        try
        {
            linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _lifetime.Token);
            admissionLease = await _scenePreparationAdmission.EnterAsync(
                documentOwner, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            ScenePreparationLease lease = new(
                admissionLease, linked, this, _lifetime, Stopwatch.GetTimestamp());
            admissionLease = null;
            linked = null;
            Interlocked.Increment(ref _scenePreparations);
            return lease;
        }
        catch
        {
            try
            {
                admissionLease?.Dispose();
            }
            finally
            {
                try
                {
                    linked?.Dispose();
                }
                finally
                {
                    _lifetime.RetireWork();
                }
            }
            throw;
        }
    }

    internal void RecordCpuPreparation(long started) =>
        Interlocked.Add(ref _cpuPreparationMilliseconds,
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);

    internal void RecordScenePreparation(long started) =>
        Interlocked.Add(ref _scenePreparationMilliseconds,
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);

    [Conditional("DEBUG")]
    internal static void LogLeaseCleanupFailure(string resource, Exception exception)
    {
        try
        {
            MarkdownDiagnostics.WriteLine(
                $"[MarkdownPerformanceSession] {resource} cleanup failed: {exception.GetType().Name}");
        }
        catch
        {
        }
    }
}

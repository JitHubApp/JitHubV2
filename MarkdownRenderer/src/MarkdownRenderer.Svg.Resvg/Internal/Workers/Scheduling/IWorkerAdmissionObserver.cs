namespace MarkdownRenderer.Svg.Resvg.Internal.Workers.Scheduling;

/// <summary>
/// Observes requests for a worker slot. <see cref="WorkerScheduler"/> calls it
/// on the requesting thread as each operation asks for admission, before the
/// scheduler checks that the pool is still available and before it takes its
/// state lock. A host composes one through <see cref="WorkerPool"/> to trace or
/// pace admission; the production renderer composes none.
/// </summary>
internal interface IWorkerAdmissionObserver
{
    /// <summary>Called when an operation asks for a worker slot.</summary>
    /// <param name="operation">The operation that will run on the slot.</param>
    /// <param name="cancellationToken">The token that cancels this admission.</param>
    void AdmissionRequested(WorkerOperation operation, CancellationToken cancellationToken);
}

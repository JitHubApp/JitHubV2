using System;
using System.Threading;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Identifies failed-build resource teardown faults so the owning pipeline can
/// fail shutdown closed without confusing them with an ordinary render failure.
/// </summary>
internal sealed class MarkdownLayoutRetirementException : Exception
{
    private Exception? _primaryFailure;

    internal MarkdownLayoutRetirementException(
        AggregateException innerException,
        Exception? primaryFailure = null)
        : base("One or more unpublished markdown layout resources failed to retire.", innerException)
    {
        _primaryFailure = primaryFailure;
    }

    internal Exception? PrimaryFailure => Volatile.Read(ref _primaryFailure);

    /// <summary>
    /// Transfers the original build failure to its public diagnostic observer
    /// without retaining its potentially large exception graph on the failed
    /// retirement task.
    /// </summary>
    internal Exception? TakePrimaryFailure() => Interlocked.Exchange(ref _primaryFailure, null);
}

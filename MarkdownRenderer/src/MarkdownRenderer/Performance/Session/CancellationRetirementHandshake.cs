using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Internal.Concurrency;

namespace MarkdownRenderer.Performance;

/// <summary>
/// Serializes a request's cancellation decision with cancellation-source
/// retirement. Once retirement starts, no late caller can schedule token
/// callbacks after the source's callback task has been captured.
/// </summary>
internal sealed class CancellationRetirementHandshake(RetirableCancellationTokenSource source)
{
    private readonly object _gate = new();
    private Task? _callbacks;
    private bool _retirementStarted;

    internal Task? RequestCancellation()
    {
        lock (_gate)
        {
            if (_retirementStarted)
                return _callbacks;

            return _callbacks ??= CancellationTokenSourceRetirement.RequestCancellation(source);
        }
    }

    internal Task? BeginRetirement()
    {
        lock (_gate)
        {
            _retirementStarted = true;
            return _callbacks;
        }
    }
}

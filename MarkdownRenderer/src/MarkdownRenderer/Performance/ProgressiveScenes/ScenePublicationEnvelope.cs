using System;
using System.Threading.Tasks;

namespace MarkdownRenderer.Performance.ProgressiveScenes;

/// <summary>
/// The dispatcher retains only this small state machine. A queued request
/// owns the prepared graph until either the callback starts or cancellation
/// atomically wins while still queued; an executing callback owns it until
/// its finally/acknowledgement completes.
/// </summary>
internal sealed class ScenePublicationEnvelope(QueuedScenePublication publication)
{
    private readonly object _gate = new();
    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private QueuedScenePublication? _publication = publication;
    private ScenePublicationState _state;

    internal Task Completion => _completion.Task;

    internal Action Dispatch => DispatchCore;

    internal bool IsQueued
    {
        get
        {
            lock (_gate)
                return _state == ScenePublicationState.Queued;
        }
    }

    internal bool TryMarkQueued()
    {
        lock (_gate)
        {
            if (_state != ScenePublicationState.Created)
                return false;
            _state = ScenePublicationState.Queued;
            return true;
        }
    }

    internal void CancelQueued()
    {
        bool canceled = false;
        lock (_gate)
        {
            if (_state is ScenePublicationState.Created or ScenePublicationState.Queued)
            {
                _state = ScenePublicationState.Canceled;
                _publication = null;
                canceled = true;
            }
        }

        if (canceled)
            _completion.TrySetResult();
    }

    internal Task CancelQueuedOrWaitAsync()
    {
        CancelQueued();
        return _completion.Task;
    }

    private void DispatchCore()
    {
        QueuedScenePublication? request;
        lock (_gate)
        {
            if (_state != ScenePublicationState.Queued)
                return;
            _state = ScenePublicationState.Executing;
            request = _publication;
            _publication = null;
        }

        Exception? failure = null;
        try
        {
            request?.Owner.PublishOnUiThread(request);
        }
        catch (Exception exception)
        {
            // The producer is awaiting this acknowledgement even when the
            // dispatcher invokes us asynchronously. Preserve callback
            // failures for that producer instead of letting an async UI
            // callback escape as an unhandled dispatcher exception.
            failure = exception;
        }
        finally
        {
            lock (_gate)
                _state = ScenePublicationState.Finished;
            if (failure is null)
                _completion.TrySetResult();
            else
                _completion.TrySetException(failure);
        }
    }
}

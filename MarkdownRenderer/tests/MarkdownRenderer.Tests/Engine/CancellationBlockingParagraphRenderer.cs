using MarkdownRenderer.Extensions;

namespace MarkdownRenderer.Tests.Engine;

/// <summary>
/// A paragraph renderer whose cancellation callback blocks until the test
/// releases it. The renderer itself waits for the engine to request
/// cancellation and then throws, so a test can observe engine disposal while
/// a registration is still running.
/// </summary>
internal sealed class CancellationBlockingParagraphRenderer : IDisposable
{
    internal TaskCompletionSource CallbackRegistered { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TaskCompletionSource CallbackEntered { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TaskCompletionSource CallbackExited { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal ManualResetEventSlim CallbackRelease { get; } = new();

    internal void Register(MarkdownExtensionBuilder extensionBuilder) =>
        extensionBuilder.RegisterBlock(MarkdownSyntaxKinds.Block.Paragraph, Render);

    public void Dispose() => CallbackRelease.Dispose();

    private void Render(MarkdownExtensionContext context, MarkdownContentBuilder contentBuilder)
    {
        _ = context.CancellationToken.Register(() =>
        {
            CallbackEntered.TrySetResult();
            try
            {
                if (!CallbackRelease.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException(
                        "Cancellation callback was not released.");
                }
            }
            finally
            {
                CallbackExited.TrySetResult();
            }
        });
        CallbackRegistered.TrySetResult();
        if (!SpinWait.SpinUntil(
                () => context.CancellationToken.IsCancellationRequested,
                TimeSpan.FromSeconds(5)))
        {
            throw new TimeoutException(
                "Engine disposal did not request cancellation.");
        }

        context.CancellationToken.ThrowIfCancellationRequested();
    }
}

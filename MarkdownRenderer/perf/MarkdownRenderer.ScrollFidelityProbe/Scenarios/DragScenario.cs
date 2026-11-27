using Microsoft.UI.Xaml.Media;

namespace MarkdownRenderer.ScrollFidelityProbe.Scenarios;

/// <summary>
/// A fast scroll-bar thumb drag: the offset advances a fixed distance on
/// every UI composition tick, without animation, like a thumb pulled
/// steadily down a long document.
/// </summary>
internal sealed class DragScenario : IScrollScenario
{
    private const double DipsPerTick = 180;
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(3);

    public string Name => "drag";

    public string Description => $"{DipsPerTick} DIPs per UI tick for {Duration.TotalSeconds:0} s, unanimated";

    public async Task RunAsync(ScenarioContext context, ScrollRecorder recorder)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DateTime end = DateTime.UtcNow + Duration;
        double offset = context.ScrollViewer.VerticalOffset;
        EventHandler<object>? handler = null;
        handler = (_, _) =>
        {
            if (DateTime.UtcNow >= end || context.Cancellation.IsCancellationRequested ||
                offset >= context.MaximumOffset)
            {
                CompositionTarget.Rendering -= handler;
                done.TrySetResult();
                return;
            }

            offset = Math.Min(context.MaximumOffset, offset + DipsPerTick);
            context.ScrollViewer.ChangeView(null, offset, null, disableAnimation: true);
        };
        CompositionTarget.Rendering += handler;
        await done.Task;
    }
}

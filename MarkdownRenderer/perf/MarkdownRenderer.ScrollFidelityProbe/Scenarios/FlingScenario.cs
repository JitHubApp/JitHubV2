namespace MarkdownRenderer.ScrollFidelityProbe.Scenarios;

/// <summary>
/// Animated far scrolls: each ChangeView asks the compositor to travel
/// eight viewports, which it does at fling speed without consulting the UI
/// thread. Content has to be ready before it arrives.
/// </summary>
internal sealed class FlingScenario : IScrollScenario
{
    private const int Flings = 6;
    private const double ViewportsPerFling = 8;

    public string Name => "fling";

    public string Description => $"{Flings} animated ChangeView flings of {ViewportsPerFling} viewports, 700 ms apart";

    public async Task RunAsync(ScenarioContext context, ScrollRecorder recorder)
    {
        for (int fling = 0; fling < Flings; fling++)
        {
            double target = Math.Min(
                context.MaximumOffset,
                context.ScrollViewer.VerticalOffset + context.ScrollViewer.ViewportHeight * ViewportsPerFling);
            context.ScrollViewer.ChangeView(null, target, null, disableAnimation: false);
            recorder.MarkJumpLanded();
            await Task.Delay(700, context.Cancellation);
        }
    }
}

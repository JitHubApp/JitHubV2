namespace MarkdownRenderer.ScrollFidelityProbe.Scenarios;

/// <summary>
/// Unanimated jumps to seeded random offsets anywhere in the document, like
/// clicks on the scroll bar track or a table-of-contents link. The report
/// measures how long each landing stays unrendered.
/// </summary>
internal sealed class JumpScenario : IScrollScenario
{
    internal const int HoldMilliseconds = 900;
    private const int Jumps = 10;

    public string Name => "jump";

    public string Description => $"{Jumps} unanimated jumps to random offsets, {HoldMilliseconds} ms apart";

    public async Task RunAsync(ScenarioContext context, ScrollRecorder recorder)
    {
        var random = new Random(4242);
        for (int jump = 0; jump < Jumps; jump++)
        {
            double target = random.NextDouble() * context.MaximumOffset;
            context.ScrollViewer.ChangeView(null, target, null, disableAnimation: true);
            recorder.MarkJumpLanded();
            await Task.Delay(HoldMilliseconds, context.Cancellation);
        }
    }
}

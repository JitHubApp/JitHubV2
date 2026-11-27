namespace MarkdownRenderer.ScrollFidelityProbe.Scenarios;

/// <summary>One way of scrolling a freshly loaded long document fast.</summary>
internal interface IScrollScenario
{
    string Name { get; }

    string Description { get; }

    /// <summary>
    /// Scrolls the document, recording the instant every discrete jump lands
    /// in <paramref name="recorder"/>. Returns when the input has finished;
    /// the run keeps capturing while the renderer settles.
    /// </summary>
    Task RunAsync(ScenarioContext context, ScrollRecorder recorder);
}

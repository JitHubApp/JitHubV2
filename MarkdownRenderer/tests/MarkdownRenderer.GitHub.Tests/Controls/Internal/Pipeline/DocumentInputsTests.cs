using Markdig;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Controls.Internal.Pipeline;
using MarkdownRenderer.Controls.Internal.Threading;
using MarkdownRenderer.Document;
using MarkdownRenderer.GitHub.Tests.TestSupport.Fakes;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using Xunit;
using RebuildReason = MarkdownRenderer.Controls.Internal.Pipeline.Rebuild.MarkdownRebuildCoordinatorReason;

namespace MarkdownRenderer.GitHub.Tests.Controls.Internal.Pipeline;

/// <summary>
/// The document inputs keep disclosure states across restyles of one
/// document, forget them when the document or parser configuration changes,
/// apply off-thread disclosure toggles on the UI thread, and treat only a
/// host-supplied Document as authoritative over Markdown.
/// </summary>
public sealed class DocumentInputsTests
{
    [Fact]
    public void ToggledDisclosureIsRecordedRaisedAndRestyled()
    {
        var host = new InputsHost();
        DocumentInputs inputs = host.Create();

        inputs.SetDisclosureExpanded(Disclosure("details-1", expanded: false), expanded: true);

        Assert.Equal(new Dictionary<string, bool> { ["details-1"] = true }, inputs.CopyDisclosureStates());
        Assert.Equal([true], host.DisclosureToggles);
        Assert.Equal([RebuildReason.Restyle], host.Rebuilds);
    }

    [Fact]
    public void CopiedStatesAreASnapshotForOnePipeline()
    {
        var host = new InputsHost();
        DocumentInputs inputs = host.Create();
        inputs.SetDisclosureExpanded("details-1", expanded: true);

        Dictionary<string, bool> copy = inputs.CopyDisclosureStates();
        inputs.SetDisclosureExpanded("details-2", expanded: false);

        Assert.Equal(new Dictionary<string, bool> { ["details-1"] = true }, copy);
        Assert.Equal(2, inputs.CopyDisclosureStates().Count);
        Assert.Equal(StringComparer.Ordinal, copy.Comparer);
    }

    [Fact]
    public void OffThreadToggleIsPostedAndAppliedOnTheUiThread()
    {
        var scheduler = new ManualUiScheduler { OnUiThread = false };
        var host = new InputsHost();
        DocumentInputs inputs = host.Create(scheduler);

        inputs.SetDisclosureExpanded("details-1", expanded: true);

        Assert.Empty(inputs.CopyDisclosureStates());
        Assert.Empty(host.Rebuilds);
        Assert.Equal([MarkdownUiPriority.Normal], scheduler.Posted);

        Assert.Equal(1, scheduler.RunPending());

        Assert.True(inputs.CopyDisclosureStates()["details-1"]);
        Assert.Equal([true], host.DisclosureToggles);
        Assert.Equal([RebuildReason.Restyle], host.Rebuilds);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("details-1", true)]
    public void OnlyAChangedDisclosureRunToggles(string? disclosureId, bool alreadyExpanded)
    {
        var host = new InputsHost();
        DocumentInputs inputs = host.Create();

        inputs.SetDisclosureExpanded(Disclosure(disclosureId, alreadyExpanded), expanded: alreadyExpanded);

        Assert.Empty(inputs.CopyDisclosureStates());
        Assert.Empty(host.DisclosureToggles);
        Assert.Empty(host.Rebuilds);
    }

    [Theory]
    [InlineData("markdown")]
    [InlineData("document")]
    [InlineData("parser")]
    [InlineData("engine")]
    public void ADocumentOrParserChangeForgetsDisclosureStatesAndRebuilds(string change)
    {
        var host = new InputsHost();
        DocumentInputs inputs = host.Create();
        inputs.SetDisclosureExpanded("details-1", expanded: true);
        host.Rebuilds.Clear();

        switch (change)
        {
            case "markdown": inputs.OnMarkdownChanged(); break;
            case "document": inputs.OnDocumentChanged(Parse("# Host")); break;
            case "parser": inputs.OnParserConfigurationChanged(); break;
            default: inputs.OnEngineChanged(null, null); break;
        }

        Assert.Empty(inputs.CopyDisclosureStates());
        Assert.Equal([RebuildReason.Full], host.Rebuilds);
        Assert.Equal(1, host.CacheClears);
    }

    [Fact]
    public void SourceAndDocumentReplacementResetInput()
    {
        var host = new InputsHost();
        DocumentInputs inputs = host.Create();

        inputs.OnMarkdownChanged();
        inputs.OnDocumentChanged(Parse("# Host"));

        Assert.Equal(2, host.InputResets);
    }

    [Fact]
    public void PublishedDocumentIsNotAHostAssignmentAndASourceChangeClearsIt()
    {
        var host = new InputsHost { Markdown = "# Source" };
        DocumentInputs inputs = host.Create();
        inputs.SetDisclosureExpanded("details-1", expanded: true);
        host.Rebuilds.Clear();
        MarkdownDocument parsed = Parse("# Source");

        inputs.PublishParsedDocument(parsed);

        Assert.Same(parsed, host.Document);
        Assert.False(inputs.HasExplicitDocument);
        Assert.Single(inputs.CopyDisclosureStates());
        Assert.Empty(host.Rebuilds);

        host.Markdown = "# Edited";
        inputs.OnMarkdownChanged();

        Assert.Null(host.Document);
        Assert.False(inputs.HasExplicitDocument);
        Assert.Equal("# Edited", inputs.EffectiveSource);
        Assert.Equal([RebuildReason.Full], host.Rebuilds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HostDocumentTakesPrecedenceAndSurvivesSourceAndParserChanges(bool parserChange)
    {
        var host = new InputsHost { Markdown = "# Source" };
        DocumentInputs inputs = host.Create();
        MarkdownDocument hostDocument = Parse("# Host");

        host.AssignDocument(hostDocument);
        if (parserChange)
            inputs.OnParserConfigurationChanged();
        else
            inputs.OnMarkdownChanged();

        Assert.True(inputs.HasExplicitDocument);
        Assert.Same(hostDocument, host.Document);
        Assert.Equal("# Host", inputs.EffectiveSource);
    }

    [Fact]
    public void ParserChangeClearsAPublishedDocument()
    {
        var host = new InputsHost { Markdown = "# Source" };
        DocumentInputs inputs = host.Create();
        inputs.PublishParsedDocument(Parse("# Source"));

        inputs.OnParserConfigurationChanged();

        Assert.Null(host.Document);
        Assert.False(inputs.HasExplicitDocument);
    }

    [Fact]
    public void ClearingTheHostDocumentReturnsToSourceParsing()
    {
        var host = new InputsHost { Markdown = "# Source" };
        DocumentInputs inputs = host.Create();
        host.AssignDocument(Parse("# Host"));

        host.AssignDocument(null);

        Assert.False(inputs.HasExplicitDocument);
        Assert.Equal("# Source", inputs.EffectiveSource);
    }

    private static LinkRun Disclosure(string? id, bool expanded) =>
        new("Details", "#details") { DisclosureId = id, IsExpanded = expanded };

    private static MarkdownDocument Parse(string source) =>
        MarkdownDocument.FromParsed(source, Markdig.Markdown.Parse(source));

    /// <summary>
    /// A host whose Document property calls back into the inputs on every
    /// change, as the dependency property's change callback does.
    /// </summary>
    private sealed class InputsHost : IDocumentInputsHost
    {
        private DocumentInputs? _inputs;

        internal List<RebuildReason> Rebuilds { get; } = [];

        internal List<bool> DisclosureToggles { get; } = [];

        internal int CacheClears { get; private set; }

        internal int InputResets { get; private set; }

        public string? Markdown { get; set; }

        public MarkdownDocument? Document { get; private set; }

        public Task? ActivePipeline => null;

        internal DocumentInputs Create(IMarkdownUiScheduler? scheduler = null) =>
            _inputs = new DocumentInputs(this, scheduler, new SnapshotRetirementTracker());

        /// <summary>Assigns Document as the host app would.</summary>
        internal void AssignDocument(MarkdownDocument? document)
        {
            if (ReferenceEquals(Document, document))
                return;
            Document = document;
            _inputs!.OnDocumentChanged(document);
        }

        public void AssignEngine(MarkdownEngine engine) =>
            throw new InvalidOperationException("These tests do not assign engines.");

        public void ClearDocument() => AssignDocument(null);

        public void SetDocument(MarkdownDocument document) => AssignDocument(document);

        public void ResetInputForDocumentReplacement() => InputResets++;

        public void ClearDocumentCaches() => CacheClears++;

        public void RequestRebuild(RebuildReason reason) => Rebuilds.Add(reason);

        public void RaiseDisclosureToggled(bool expanded) => DisclosureToggles.Add(expanded);
    }
}

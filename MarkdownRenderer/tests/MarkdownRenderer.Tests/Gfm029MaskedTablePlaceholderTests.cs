using Markdig;
using Markdig.Extensions.Tables;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class Gfm029MaskedTablePlaceholderTests
{
    private const char MaskedTableSeparator = '\uE000';

    private static MarkdownPipeline CreatePipeline() =>
        new MarkdownPipelineBuilder()
            .UsePipeTables()
            .Build();

    private static string RenderPrepared(string source, out bool wasPrepared)
    {
        MarkdownPipeline pipeline = CreatePipeline();
        Gfm029Compatibility.PreparedSource prepared =
            Gfm029Compatibility.PrepareSource(source, enableTables: true);
        wasPrepared = prepared.HasChanges;

        var document = Markdown.Parse(prepared.Text, pipeline);
        Gfm029Compatibility.NormalizeSyntaxTree(
            document,
            source,
            enableAutoLinks: false,
            prepared);

        return Gfm029Compatibility.RenderReferenceHtml(
            document,
            pipeline,
            applyTagFilter: false);
    }

    [Fact]
    public void AuthoredPrivateUseCharacterSurvivesWhenTablePreparationDoesNotRun()
    {
        string source = $"Authored marker: {MaskedTableSeparator}\n";

        string html = RenderPrepared(source, out bool wasPrepared);

        Assert.False(wasPrepared);
        Assert.Contains($"Authored marker: {MaskedTableSeparator}", html);
        Assert.Equal(1, html.Count(character => character == MaskedTableSeparator));
    }

    [Fact]
    public void MismatchedTableRestoresOnlyItsMaskedDashAndPreservesAuthoredPrivateUseCharacter()
    {
        string source =
            $"| Name | Value |\n|---|\n\nAuthored marker: {MaskedTableSeparator}\n";

        string html = RenderPrepared(source, out bool wasPrepared);

        Assert.True(wasPrepared);
        Assert.Contains("|---|", html);
        Assert.Contains($"Authored marker: {MaskedTableSeparator}", html);
        Assert.Equal(1, html.Count(character => character == MaskedTableSeparator));
    }

    [Fact]
    public void InsertedTableSeparatorDoesNotShiftMaskedPlaceholderRestoration()
    {
        string source =
            "| Prefix | Value |\n| --- | --- |\n| alpha | beta |\nrow-without-a-pipe\n\n" +
            $"| Name | Value |\n|---|\n\nAuthored marker: {MaskedTableSeparator}\n";

        string html = RenderPrepared(source, out bool wasPrepared);

        Assert.True(wasPrepared);
        Assert.Contains("|---|", html);
        Assert.Contains($"Authored marker: {MaskedTableSeparator}", html);
        Assert.Equal(1, html.Count(character => character == MaskedTableSeparator));
    }
}

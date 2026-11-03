using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Markdig;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;

namespace MarkdownRenderer;

/// <summary>
/// Turns source into an immutable <see cref="MarkdownDocument"/> with one
/// engine's frozen profile, extension set and pipeline. It holds no admission
/// or cache state; <see cref="MarkdownParseCoordinator"/> decides when it runs.
/// </summary>
internal sealed class MarkdownDocumentParser
{
    private static readonly IReadOnlyList<MarkdownDiagnostic> _noDiagnostics =
        Array.AsReadOnly(Array.Empty<MarkdownDiagnostic>());

    private readonly MarkdownProfile _profile;
    private readonly MarkdownExtensionSet _extensions;
    private readonly IReadOnlyList<Action<MarkdownPipelineBuilder>> _pipelineConfigurations;
    private readonly IMarkdownPresentationConfiguration? _presentationConfiguration;
    private readonly MarkdownPipeline _pipeline;
    private MarkdownPipeline? _specificationValidationPipeline;

    internal MarkdownDocumentParser(
        MarkdownProfile profile,
        MarkdownExtensionSet extensions,
        IReadOnlyList<Action<MarkdownPipelineBuilder>> pipelineConfigurations,
        IMarkdownPresentationConfiguration? presentationConfiguration)
    {
        _profile = profile;
        _extensions = extensions;
        _pipelineConfigurations = pipelineConfigurations;
        _presentationConfiguration = presentationConfiguration;
        _pipeline = MarkdownPipelineFactory.Build(profile, extensions, pipelineConfigurations);
    }

    internal async ValueTask<MarkdownDocument> ParseAsync(
        string source,
        bool deferOffscreenScenes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool isGfm029 =
            (_profile.FeatureFlags & MarkdownProfileFeatures.Gfm029Marker) != 0;
        Gfm029Compatibility.PreparedSource prepared = isGfm029
            ? Gfm029Compatibility.PrepareSource(
                source,
                enableTables:
                    (_profile.FeatureFlags & MarkdownProfileFeatures.PipeTables) != 0)
            : Gfm029Compatibility.PreparedSource.Unchanged(source);
        var parsedDocument = Markdown.Parse(prepared.Text, _pipeline);
        FalseTaskListMarkers.Normalize(parsedDocument, prepared.Text, _pipeline);
        if (isGfm029)
        {
            Gfm029Compatibility.NormalizeSyntaxTree(
                parsedDocument,
                source,
                enableAutoLinks:
                    (_profile.FeatureFlags & MarkdownProfileFeatures.AutoLinks) != 0,
                prepared: prepared);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return await MarkdownDocument.FromParsedAsync(
                source,
                parsedDocument,
                _noDiagnostics,
                _extensions,
                cancellationToken,
                _presentationConfiguration,
                deferOffscreenScenes)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Renders with the selected profile's specification syntax while allowing
    /// raw HTML passthrough solely for the official-example conformance oracle.
    /// Production parsing continues to enforce the profile's literal-HTML policy.
    /// </summary>
    internal string RenderSpecificationHtmlForValidation(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        MarkdownPipeline pipeline = _specificationValidationPipeline ??=
            MarkdownPipelineFactory.Build(
                _profile,
                MarkdownExtensionSet.Empty,
                _pipelineConfigurations,
                enforceHtmlPolicy: false);
        bool isGfm029 =
            (_profile.FeatureFlags & MarkdownProfileFeatures.Gfm029Marker) != 0;
        Gfm029Compatibility.PreparedSource prepared = isGfm029
            ? Gfm029Compatibility.PrepareSource(
                source,
                enableTables:
                    (_profile.FeatureFlags & MarkdownProfileFeatures.PipeTables) != 0)
            : Gfm029Compatibility.PreparedSource.Unchanged(source);
        var parsedDocument = Markdown.Parse(prepared.Text, pipeline);
        FalseTaskListMarkers.Normalize(parsedDocument, prepared.Text, pipeline);
        if (isGfm029)
        {
            Gfm029Compatibility.NormalizeSyntaxTree(
                parsedDocument,
                source,
                enableAutoLinks:
                    (_profile.FeatureFlags & MarkdownProfileFeatures.AutoLinks) != 0,
                prepared: prepared);
        }
        return Gfm029Compatibility.RenderReferenceHtml(
            parsedDocument,
            pipeline,
            applyTagFilter:
                (_profile.FeatureFlags & MarkdownProfileFeatures.GfmTagFilter) != 0);
    }
}

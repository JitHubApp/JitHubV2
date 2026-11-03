using System;
using System.Collections.Generic;

namespace MarkdownRenderer;

/// <summary>Built-in immutable markdown parsing profiles.</summary>
public static class MarkdownProfiles
{
    private const MarkdownProfileFeatures CommonMarkFeatures = MarkdownProfileFeatures.CommonMark;
    private const MarkdownProfileFeatures GfmFeatures =
        CommonMarkFeatures |
        MarkdownProfileFeatures.PipeTables |
        MarkdownProfileFeatures.TaskLists |
        MarkdownProfileFeatures.AutoLinks |
        MarkdownProfileFeatures.Strikethrough |
        MarkdownProfileFeatures.GfmTagFilter |
        MarkdownProfileFeatures.Gfm029Marker;
    private const MarkdownProfileFeatures GitHubReadmeFeatures =
        GfmFeatures |
        MarkdownProfileFeatures.Footnotes |
        MarkdownProfileFeatures.Emoji |
        MarkdownProfileFeatures.GenericAttributes |
        MarkdownProfileFeatures.Alerts |
        MarkdownProfileFeatures.GitHubReadmeMarker;
    private const MarkdownProfileFeatures MarkdownExtraFeatures =
        CommonMarkFeatures |
        MarkdownProfileFeatures.DefinitionLists |
        MarkdownProfileFeatures.Abbreviations |
        MarkdownProfileFeatures.Figures |
        MarkdownProfileFeatures.MarkdownExtraMarker;

    /// <summary>CommonMark 0.31.2 with raw HTML preserved as literal text.</summary>
    public static MarkdownProfile CommonMark { get; } = new(
        "commonmark-0.31.2",
        "https://spec.commonmark.org/0.31.2/",
        CommonMarkFeatures,
        MarkdownHtmlMode.Literal);

    /// <summary>GitHub Flavored Markdown 0.29-gfm with raw HTML preserved as literal text.</summary>
    public static MarkdownProfile GfmStrict { get; } = new(
        "gfm-0.29",
        "https://github.github.com/gfm/",
        GfmFeatures,
        MarkdownHtmlMode.Literal);

    /// <summary>
    /// GitHub README syntax marker. Raw HTML is parsed only as input for the
    /// separately installed safe native HTML extension; it is never executed.
    /// </summary>
    public static MarkdownProfile GitHubReadme { get; } = new(
        "github-readme",
        specification: null,
        GitHubReadmeFeatures,
        MarkdownHtmlMode.SafeNativeCandidate);

    /// <summary>
    /// CommonMark plus definition lists, abbreviations, and figures. Compose it
    /// with another profile through <see cref="MarkdownProfile.Combine"/> or
    /// <see cref="MarkdownEngineBuilder.AddProfile"/>.
    /// </summary>
    public static MarkdownProfile MarkdownExtra { get; } = new(
        "markdown-extra",
        specification: null,
        MarkdownExtraFeatures,
        MarkdownHtmlMode.Literal);

    internal static MarkdownProfile GetOrCreateComposite(
        MarkdownProfileFeatures features,
        MarkdownHtmlMode htmlMode)
    {
        if (features == CommonMarkFeatures && htmlMode == MarkdownHtmlMode.Literal)
            return CommonMark;
        if (features == GfmFeatures && htmlMode == MarkdownHtmlMode.Literal)
            return GfmStrict;
        if (features == GitHubReadmeFeatures && htmlMode == MarkdownHtmlMode.SafeNativeCandidate)
            return GitHubReadme;
        if (features == MarkdownExtraFeatures && htmlMode == MarkdownHtmlMode.Literal)
            return MarkdownExtra;

        string baseId;
        string? specification;
        if ((features & MarkdownProfileFeatures.GitHubReadmeMarker) != 0)
        {
            baseId = GitHubReadme.Id;
            specification = GitHubReadme.Specification;
        }
        else if ((features & GfmFeatures) == GfmFeatures)
        {
            baseId = GfmStrict.Id;
            specification = GfmStrict.Specification;
        }
        else
        {
            baseId = CommonMark.Id;
            specification = CommonMark.Specification;
        }

        string id = (features & MarkdownProfileFeatures.MarkdownExtraMarker) != 0 &&
                    baseId != MarkdownExtra.Id
            ? $"{baseId}+markdown-extra"
            : baseId;
        return new MarkdownProfile(id, specification, features, htmlMode);
    }

    /// <summary>
    /// Creates the exact option set declared by one official 0.29-gfm
    /// conformance example. The reference runner enables extensions per fence;
    /// enabling the whole product profile would contaminate core examples.
    /// </summary>
    internal static MarkdownProfile CreateGfmConformanceProfile(
        IReadOnlyList<string> extensions)
    {
        ArgumentNullException.ThrowIfNull(extensions);
        var features = CommonMarkFeatures | MarkdownProfileFeatures.Gfm029Marker;
        foreach (string extension in extensions)
        {
            features |= extension switch
            {
                "table" => MarkdownProfileFeatures.PipeTables,
                "tasklist" => MarkdownProfileFeatures.TaskLists,
                "autolink" => MarkdownProfileFeatures.AutoLinks,
                "strikethrough" => MarkdownProfileFeatures.Strikethrough,
                "tagfilter" => MarkdownProfileFeatures.GfmTagFilter,
                _ => throw new ArgumentException(
                    $"Unsupported official GFM extension option '{extension}'.",
                    nameof(extensions)),
            };
        }

        return new MarkdownProfile(
            GfmStrict.Id,
            GfmStrict.Specification,
            features,
            MarkdownHtmlMode.Literal);
    }
}

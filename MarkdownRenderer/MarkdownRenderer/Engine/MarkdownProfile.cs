using System;
using System.Collections.Generic;

namespace MarkdownRenderer;

/// <summary>
/// Immutable parsing-profile description used to configure a <see cref="MarkdownEngine"/>.
/// Profiles identify syntax behavior without exposing the underlying parser.
/// </summary>
public sealed class MarkdownProfile : IEquatable<MarkdownProfile>
{
    private readonly IReadOnlyList<string> _features;

    internal MarkdownProfile(
        string id,
        string? specification,
        MarkdownProfileFeatures features,
        MarkdownHtmlMode htmlMode)
    {
        Id = id;
        Specification = specification;
        FeatureFlags = features;
        HtmlMode = htmlMode;
        _features = Array.AsReadOnly(GetFeatureIds(features));
    }

    /// <summary>Gets the stable, machine-readable profile identifier.</summary>
    public string Id { get; }

    /// <summary>Gets the specification URI associated with this profile, when one exists.</summary>
    public string? Specification { get; }

    /// <summary>Gets stable identifiers for the syntax features enabled by this profile.</summary>
    public IReadOnlyList<string> Features => _features;

    internal MarkdownProfileFeatures FeatureFlags { get; }

    internal MarkdownHtmlMode HtmlMode { get; }

    /// <summary>
    /// Creates an immutable profile that enables the union of this profile and
    /// <paramref name="additionalProfile"/>. This is primarily intended for
    /// composing a standards profile with <see cref="MarkdownProfiles.MarkdownExtra"/>.
    /// </summary>
    public MarkdownProfile Combine(MarkdownProfile additionalProfile)
    {
        ArgumentNullException.ThrowIfNull(additionalProfile);
        if (Equals(additionalProfile))
            return this;

        var features = FeatureFlags | additionalProfile.FeatureFlags;
        var htmlMode = HtmlMode == MarkdownHtmlMode.SafeNativeCandidate ||
            additionalProfile.HtmlMode == MarkdownHtmlMode.SafeNativeCandidate
                ? MarkdownHtmlMode.SafeNativeCandidate
                : MarkdownHtmlMode.Literal;
        return MarkdownProfiles.GetOrCreateComposite(features, htmlMode);
    }

    /// <inheritdoc />
    public bool Equals(MarkdownProfile? other)
        => other is not null &&
           FeatureFlags == other.FeatureFlags &&
           HtmlMode == other.HtmlMode;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is MarkdownProfile other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(FeatureFlags, HtmlMode);

    /// <inheritdoc />
    public override string ToString() => Id;

    private static string[] GetFeatureIds(MarkdownProfileFeatures features)
    {
        var values = new List<string>();
        Add(MarkdownProfileFeatures.CommonMark, "commonmark");
        Add(MarkdownProfileFeatures.PipeTables, "pipe-tables");
        Add(MarkdownProfileFeatures.TaskLists, "task-lists");
        Add(MarkdownProfileFeatures.AutoLinks, "autolinks");
        Add(MarkdownProfileFeatures.Strikethrough, "strikethrough");
        Add(MarkdownProfileFeatures.Footnotes, "footnotes");
        Add(MarkdownProfileFeatures.Emoji, "emoji");
        Add(MarkdownProfileFeatures.GenericAttributes, "generic-attributes");
        Add(MarkdownProfileFeatures.Alerts, "alerts");
        Add(MarkdownProfileFeatures.DefinitionLists, "definition-lists");
        Add(MarkdownProfileFeatures.Abbreviations, "abbreviations");
        Add(MarkdownProfileFeatures.Figures, "figures");
        Add(MarkdownProfileFeatures.GfmTagFilter, "disallowed-raw-html");
        return values.ToArray();

        void Add(MarkdownProfileFeatures feature, string id)
        {
            if ((features & feature) != 0)
                values.Add(id);
        }
    }
}

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

[Flags]
internal enum MarkdownProfileFeatures
{
    None = 0,
    CommonMark = 1 << 0,
    PipeTables = 1 << 1,
    TaskLists = 1 << 2,
    AutoLinks = 1 << 3,
    Strikethrough = 1 << 4,
    Footnotes = 1 << 5,
    Emoji = 1 << 6,
    GenericAttributes = 1 << 7,
    Alerts = 1 << 8,
    DefinitionLists = 1 << 9,
    Abbreviations = 1 << 10,
    Figures = 1 << 11,
    GitHubReadmeMarker = 1 << 12,
    MarkdownExtraMarker = 1 << 13,
    Gfm029Marker = 1 << 14,
    GfmTagFilter = 1 << 15,
}

internal enum MarkdownHtmlMode
{
    Literal,
    SafeNativeCandidate,
}

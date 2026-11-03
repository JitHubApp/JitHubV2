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

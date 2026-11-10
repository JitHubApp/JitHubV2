using System;
using System.Collections.Generic;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Parsing.Internal.SafeHtml;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Builds inline runs for declarative hosted-element content, including
/// materialized deferred scenes and declarative images.
/// </summary>
internal sealed class DeclarativeInlineBuilder
{
    private readonly MarkdownLayoutContext _context;
    private readonly LayoutConstructionTracker _tracker;
    private readonly MaterializedSceneScope _scenes;

    internal DeclarativeInlineBuilder(
        MarkdownLayoutContext context,
        LayoutConstructionTracker tracker,
        MaterializedSceneScope scenes)
    {
        _context = context;
        _tracker = tracker;
        _scenes = scenes;
    }

    internal bool CanBuildDeclarativeInlineContent(MarkdownContent content) => content.Kind switch
    {
        MarkdownContentKind.Text => content.Text is not null,
        MarkdownContentKind.Image => !string.IsNullOrWhiteSpace(content.Destination),
        MarkdownContentKind.VectorScene => content.VectorScene is not null,
        MarkdownContentKind.Link =>
            !string.IsNullOrWhiteSpace(content.Destination) &&
            content.Children.Count > 0 &&
            DeclarativeContentSyntax.AllChildren(content, CanBuildDeclarativeInlineContent),
        MarkdownContentKind.Container =>
            content.Children.Count > 0 && DeclarativeContentSyntax.AllChildren(content, CanBuildDeclarativeInlineContent),
        _ => false,
    };

    internal bool TryCreateDeclarativeInlineRuns(
        MarkdownContentFragment fragment,
        out IReadOnlyList<InlineRun> runs)
    {
        foreach (MarkdownContent item in fragment.Items)
        {
            if (!CanBuildDeclarativeInlineContent(item))
            {
                runs = Array.Empty<InlineRun>();
                return false;
            }
        }

        var result = new List<InlineRun>();
        foreach (MarkdownContent item in fragment.Items)
            AppendDeclarativeInline(item, result);
        runs = result;
        return result.Count > 0;
    }

    internal void AppendMaterializedSceneInline(
        string marker,
        MarkdownContentFragment fragment,
        object? projectionIdentity,
        List<InlineRun> destination)
    {
        string? previousMarker = _scenes.Marker;
        int previousOrdinal = _scenes.Ordinal;
        object? previousIdentity = _scenes.ProjectionIdentity;
        _scenes.Marker = marker;
        _scenes.Ordinal = 0;
        _scenes.ProjectionIdentity = projectionIdentity;
        try
        {
            foreach (MarkdownContent item in fragment.Items)
                AppendDeclarativeInline(item, destination);
        }
        finally
        {
            _scenes.Marker = previousMarker;
            _scenes.Ordinal = previousOrdinal;
            _scenes.ProjectionIdentity = previousIdentity;
        }
    }

    internal void AppendDeclarativeInline(MarkdownContent content, List<InlineRun> destination)
    {
        if (content.Kind == MarkdownContentKind.VectorScene &&
            content.Attributes.TryGetValue(MarkdownContentAttributes.DeferredScene, out string? marker) &&
            !string.IsNullOrWhiteSpace(marker) &&
            _context.PerformanceSession is IMarkdownPerformanceSessionInternal session &&
            _context.PerformanceDocumentOwner is { } documentOwner &&
            session.TryGetDeferredSceneResult(documentOwner, marker,
                out MarkdownContentFragment? materialized, out object? projectionIdentity) &&
            materialized is not null && materialized.Items.Count > 0)
        {
            AppendMaterializedSceneInline(marker, materialized, projectionIdentity, destination);
            return;
        }

        switch (content.Kind)
        {
            case MarkdownContentKind.Text:
                destination.Add(new TextRun(content.Text ?? string.Empty)
                {
                    ElementKey = DeclarativeContentSyntax.GetElementKey(content, string.Empty),
                    SourceSpan = content.SourceSpan,
                });
                break;
            case MarkdownContentKind.Image:
                destination.Add(CreateDeclarativeImageRun(content, linkUrl: null, linkTitle: null));
                break;
            case MarkdownContentKind.VectorScene:
                if (content.VectorScene is { } scene)
                    destination.Add(CreateDeclarativeVectorRun(content, scene));
                break;
            case MarkdownContentKind.Link:
                destination.Add(CreateDeclarativeLinkRun(content));
                break;
            case MarkdownContentKind.Container:
                foreach (MarkdownContent child in content.Children)
                    AppendDeclarativeInline(child, destination);
                break;
        }
    }

    // The run is registered for construction cleanup before it is published.
    private InlineVectorSceneRun CreateDeclarativeVectorRun(MarkdownContent content, MarkdownVectorScene scene)
    {
        string elementKey = DeclarativeContentSyntax.GetElementKey(
            content,
            content.AccessibilityRole == MarkdownAccessibilityRole.Math
                ? MarkdownElementKeys.Math
                : MarkdownElementKeys.Diagram);
        string? deferredMarker = DeclarativeContentSyntax.GetAttribute(content, MarkdownContentAttributes.DeferredScene);
        var run = new InlineVectorSceneRun(
            _context,
            scene,
            content.SemanticText ?? string.Empty,
            content.AccessibilityName ?? content.SemanticText ?? string.Empty,
            content.AccessibilityDescription,
            elementKey)
        {
            ElementKey = elementKey,
            SourceSpan = content.SourceSpan,
            DeferredSceneMarker = deferredMarker,
            DeferredSceneBinding = deferredMarker is null
                ? _scenes.NextBinding()
                : null,
        };
        _context.Construction.RegisterResource(run);
        return run;
    }

    // A link whose only child is an image renders as a linked image run.
    private InlineRun CreateDeclarativeLinkRun(MarkdownContent content)
    {
        if (content.Children.Count == 1 && content.Children[0].Kind == MarkdownContentKind.Image)
        {
            return CreateDeclarativeImageRun(
                content.Children[0],
                content.Destination,
                DeclarativeContentSyntax.GetAttribute(content, MarkdownContentAttributes.Title));
        }

        return new LinkRun(
            DeclarativeContentSyntax.CollectDeclarativeText(content),
            content.Destination ?? string.Empty,
            DeclarativeContentSyntax.GetAttribute(content, MarkdownContentAttributes.Title))
        {
            AccessibilityName = content.AccessibilityName,
            ElementKey = DeclarativeContentSyntax.GetElementKey(content, MarkdownElementKeys.Link),
            SourceSpan = content.SourceSpan,
        };
    }

    private InlineImageRun CreateDeclarativeImageRun(
        MarkdownContent image,
        string? linkUrl,
        string? linkTitle)
    {
        DeclarativeContentSyntax.TryGetDeclarativeLength(image, MarkdownContentAttributes.ImageWidth, out SafeHtmlLength width);
        DeclarativeContentSyntax.TryGetDeclarativeLength(image, MarkdownContentAttributes.ImageHeight, out SafeHtmlLength height);
        return _tracker.TrackInlineImageRun(new InlineImageRun(
            _context,
            image.Text ?? string.Empty,
            image.Destination ?? string.Empty,
            DeclarativeContentSyntax.GetAttribute(image, MarkdownContentAttributes.Title),
            linkUrl,
            linkTitle,
            width.Value > 0 ? width : null,
            height.Value > 0 ? height : null)
        {
            SourceSpan = image.SourceSpan,
        });
    }
}

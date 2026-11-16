using System;
using System.Collections.Generic;
using System.Threading;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Extensions.Vectors;

namespace MarkdownRenderer.Engine.Internal;

/// <summary>
/// Estimates the retained managed size of a parsed document for the engine's
/// completed-parse budget. Extension content is walked iteratively so
/// extension-controlled nesting cannot exhaust the call stack.
/// </summary>
internal static class MarkdownDocumentWeightEstimator
{
    private const long BaseDocumentWeightBytes = 1024;
    private const long SemanticEntryWeightBytes = 64;
    private const long SourceMapEntryWeightBytes = 40;
    private const long Utf16SyntaxWeightPerCharacter = 6;
    private const long ExtensionMapEntryWeightBytes = 48;
    private const long ExtensionFragmentWeightBytes = 32;
    private const long ExtensionContentWeightBytes = 112;
    private const long ExtensionAttributeWeightBytes = 48;
    private const long VectorSceneWeightBytes = 64;
    private const long VectorCommandWeightBytes = 72;
    private const long VectorPathOperationWeightBytes = 56;
    private const long VectorStyleWeightBytes = 64;
    private const long VectorSemanticWeightBytes = 80;
    private const long VectorLinkWeightBytes = 40;

    /// <summary>
    /// Estimates a document for a cache with <paramref name="budgetBytes"/>.
    /// When the fixed and source-proportional cost alone already exceed the
    /// budget, that lower bound is returned without walking the document.
    /// </summary>
    internal static long EstimateForBudget(
        MarkdownDocument document,
        long budgetBytes,
        CancellationToken cancellationToken)
    {
        long minimumWeight = AddSaturating(
            BaseDocumentWeightBytes,
            (long)document.Source.Length * Utf16SyntaxWeightPerCharacter);
        return minimumWeight > budgetBytes
            ? minimumWeight
            : Estimate(document, cancellationToken);
    }

    internal static long Estimate(
        MarkdownDocument document,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);

        long weight = BaseDocumentWeightBytes;
        weight = AddSaturating(weight, (long)document.Source.Length * Utf16SyntaxWeightPerCharacter);
        weight = AddSaturating(weight, (long)document.SourceMap.Count * SourceMapEntryWeightBytes);

        foreach (var heading in document.GetHeadings())
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddSemanticEntry(ref weight, heading.DisplayText);
        }
        foreach (var link in document.GetLinks())
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddSemanticEntry(ref weight, link.DisplayText, link.Url, link.Title);
        }
        foreach (var codeBlock in document.GetCodeBlocks())
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddSemanticEntry(ref weight, codeBlock.DisplayText, codeBlock.Language);
        }
        foreach (var image in document.GetImages())
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddSemanticEntry(ref weight, image.DisplayText, image.Source, image.AltText, image.Title);
        }
        foreach (var footnote in document.GetFootnotes())
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddSemanticEntry(ref weight, footnote.Label, footnote.DisplayText);
        }
        foreach (var definition in document.GetDefinitionItems())
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddSemanticEntry(ref weight, definition.Term, definition.Definition);
        }
        foreach (var abbreviation in document.GetAbbreviations())
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddSemanticEntry(ref weight, abbreviation.DisplayText, abbreviation.Expansion);
        }
        foreach (var fragment in document.GetFragments())
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddSemanticEntry(ref weight, fragment.Id);
        }
        foreach (var diagnostic in document.Diagnostics)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddSemanticEntry(ref weight, diagnostic.Code, diagnostic.Message);
        }

        AddExtensionContent(
            ref weight,
            document.ExtensionBlockNodeContent,
            cancellationToken);
        AddExtensionContent(
            ref weight,
            document.ExtensionInlineNodeContent,
            cancellationToken);
        weight = AddSaturating(
            weight,
            (long)(document.ExtensionBlockContent.Count + document.ExtensionInlineContent.Count) *
            ExtensionMapEntryWeightBytes);

        return weight;
    }

    private static void AddSemanticEntry(
        ref long weight,
        string? first = null,
        string? second = null,
        string? third = null,
        string? fourth = null)
    {
        weight = AddSaturating(weight, SemanticEntryWeightBytes);
        AddStringWeight(ref weight, first);
        AddStringWeight(ref weight, second);
        AddStringWeight(ref weight, third);
        AddStringWeight(ref weight, fourth);
    }

    private static void AddExtensionContent<TKey>(
        ref long weight,
        IReadOnlyDictionary<TKey, MarkdownContentFragment> contentByNode,
        CancellationToken cancellationToken)
        where TKey : notnull
    {
        foreach (var entry in contentByNode)
        {
            cancellationToken.ThrowIfCancellationRequested();
            weight = AddSaturating(weight, ExtensionMapEntryWeightBytes);
            weight = AddSaturating(weight, ExtensionFragmentWeightBytes);

            // Iterative traversal keeps extension-controlled nesting from
            // consuming the managed call stack during cache accounting.
            var pending = new Stack<MarkdownContent>(entry.Value.Items.Count);
            for (int index = entry.Value.Items.Count - 1; index >= 0; index--)
                pending.Push(entry.Value.Items[index]);

            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                MarkdownContent node = pending.Pop();
                weight = AddSaturating(weight, ExtensionContentWeightBytes);
                AddStringWeight(ref weight, node.StyleRole.Name);
                AddStringWeight(ref weight, node.Text);
                AddStringWeight(ref weight, node.Destination);
                AddStringWeight(ref weight, node.Language);
                AddStringWeight(ref weight, node.FactoryKey);
                AddStringWeight(ref weight, node.CustomKind);
                AddStringWeight(ref weight, node.AccessibilityName);
                AddStringWeight(ref weight, node.AccessibilityDescription);
                AddStringWeight(ref weight, node.SemanticText);
                if (node.VectorScene is { } vectorScene)
                    AddVectorSceneWeight(ref weight, vectorScene, cancellationToken);

                foreach (var attribute in node.Attributes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    weight = AddSaturating(weight, ExtensionAttributeWeightBytes);
                    AddStringWeight(ref weight, attribute.Key);
                    AddStringWeight(ref weight, attribute.Value);
                }

                for (int index = node.Children.Count - 1; index >= 0; index--)
                    pending.Push(node.Children[index]);
            }
        }
    }

    private static void AddStringWeight(ref long weight, string? value)
    {
        if (value is not null)
            weight = AddSaturating(weight, (long)value.Length * sizeof(char));
    }

    private static void AddVectorSceneWeight(
        ref long weight,
        MarkdownVectorScene scene,
        CancellationToken cancellationToken)
    {
        weight = AddSaturating(weight, VectorSceneWeightBytes);
        for (int commandIndex = 0; commandIndex < scene.Commands.Count; commandIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MarkdownVectorCommand command = scene.Commands[commandIndex];
            weight = AddSaturating(weight, VectorCommandWeightBytes);
            weight = AddSaturating(
                weight,
                (long)command.Path.Count * VectorPathOperationWeightBytes);
            AddStringWeight(ref weight, command.Text);
            if (command.TextStyle is { } textStyle)
                AddStringWeight(ref weight, textStyle.FontFamily);
            if (command.Style is { } style)
            {
                weight = AddSaturating(weight, VectorStyleWeightBytes);
                weight = AddSaturating(weight, (long)style.DashArray.Count * sizeof(float));
            }
        }

        for (int semanticIndex = 0; semanticIndex < scene.Semantics.Count; semanticIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MarkdownVectorSemanticItem semantic = scene.Semantics[semanticIndex];
            weight = AddSaturating(weight, VectorSemanticWeightBytes);
            AddStringWeight(ref weight, semantic.Name);
            AddStringWeight(ref weight, semantic.Description);
        }

        for (int linkIndex = 0; linkIndex < scene.Links.Count; linkIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MarkdownVectorLinkAction link = scene.Links[linkIndex];
            weight = AddSaturating(weight, VectorLinkWeightBytes);
            AddStringWeight(ref weight, link.Target);
            AddStringWeight(ref weight, link.Action);
        }
    }

    private static long AddSaturating(long left, long right)
        => right >= long.MaxValue - left ? long.MaxValue : left + right;
}

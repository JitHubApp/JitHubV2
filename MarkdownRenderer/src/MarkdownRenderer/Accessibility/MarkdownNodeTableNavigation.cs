using System.Collections.Generic;
using Microsoft.UI.Xaml.Automation.Provider;

namespace MarkdownRenderer.Accessibility;

/// <summary>
/// Grid and table pattern lookups for table and table-cell node peers.
/// </summary>
internal static class MarkdownNodeTableNavigation
{
    internal static IRawElementProviderSimple GetItem(
        MarkdownAutomationPeer root,
        MarkdownSemanticNode node,
        int row,
        int column)
    {
        foreach (var child in node.Children)
        {
            if (child.Role == MarkdownSemanticRole.TableCell &&
                child.Row == row &&
                child.Column == column &&
                root.TryGetProviderForSemanticNode(child, out var provider))
            {
                return provider;
            }
        }

        return null!;
    }

    internal static IRawElementProviderSimple[] GetColumnHeaders(
        MarkdownAutomationPeer root,
        MarkdownSemanticNode node)
    {
        var providers = new List<IRawElementProviderSimple>();
        foreach (var child in node.Children)
        {
            if (child.Role == MarkdownSemanticRole.TableCell &&
                child.IsHeader &&
                root.TryGetProviderForSemanticNode(child, out var provider))
            {
                providers.Add(provider);
            }
        }

        return providers.ToArray();
    }

    internal static IRawElementProviderSimple GetContainingGrid(
        MarkdownAutomationPeer root,
        MarkdownSemanticNode node)
    {
        var table = FindAncestorTable(node);
        return table is not null && root.TryGetProviderForSemanticNode(table, out var provider)
            ? provider
            : null!;
    }

    internal static IRawElementProviderSimple[] GetColumnHeaderItems(
        MarkdownAutomationPeer root,
        MarkdownSemanticNode node)
    {
        var table = FindAncestorTable(node);
        if (table is null) return System.Array.Empty<IRawElementProviderSimple>();

        var providers = new List<IRawElementProviderSimple>();
        foreach (var child in table.Children)
        {
            if (child.Role == MarkdownSemanticRole.TableCell &&
                child.IsHeader &&
                child.Column == node.Column &&
                root.TryGetProviderForSemanticNode(child, out var provider))
            {
                providers.Add(provider);
            }
        }

        return providers.ToArray();
    }

    private static MarkdownSemanticNode? FindAncestorTable(MarkdownSemanticNode node)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            if (current.Role == MarkdownSemanticRole.Table) return current;
        }

        return null;
    }
}

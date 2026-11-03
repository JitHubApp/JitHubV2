using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using MarkdownRenderer.Extensions;

namespace MarkdownRenderer.Document;

/// <summary>
/// Copies builder collections into the read-only snapshots a
/// <see cref="MarkdownDocument"/> exposes.
/// </summary>
internal static class DocumentSnapshot
{
    internal static IReadOnlyList<T> Freeze<T>(IReadOnlyList<T> values)
    {
        if (values.Count == 0)
            return Array.AsReadOnly(Array.Empty<T>());

        var snapshot = new T[values.Count];
        for (int i = 0; i < values.Count; i++)
            snapshot[i] = values[i];
        return Array.AsReadOnly(snapshot);
    }

    internal static IReadOnlyDictionary<SourceSpan, IReadOnlyList<MarkdownContentFragment>> FreezeExtensionContent(
        IReadOnlyDictionary<SourceSpan, List<MarkdownContentFragment>>? content)
    {
        var snapshot = new Dictionary<SourceSpan, IReadOnlyList<MarkdownContentFragment>>();
        if (content is not null)
        {
            foreach (var entry in content)
                snapshot.Add(entry.Key, Freeze(entry.Value));
        }

        return new ReadOnlyDictionary<SourceSpan, IReadOnlyList<MarkdownContentFragment>>(snapshot);
    }

    internal static IReadOnlyDictionary<TNode, MarkdownContentFragment> FreezeExtensionNodeContent<TNode>(
        IReadOnlyDictionary<TNode, MarkdownContentFragment>? content)
        where TNode : class
        => new ReadOnlyDictionary<TNode, MarkdownContentFragment>(
            content is null || content.Count == 0
                ? new Dictionary<TNode, MarkdownContentFragment>(ReferenceEqualityComparer.Instance)
                : new Dictionary<TNode, MarkdownContentFragment>(content, ReferenceEqualityComparer.Instance));
}

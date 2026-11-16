using System;
using System.Collections.Generic;
using MarkdownRenderer.Extensions.Internal;

namespace MarkdownRenderer.Extensions;

/// <summary>An immutable content fragment produced by one extension renderer.</summary>
public sealed class MarkdownContentFragment
{
    private static readonly IReadOnlyList<MarkdownContent> EmptyItems = Array.Empty<MarkdownContent>();

    internal MarkdownContentFragment(
        IReadOnlyList<MarkdownContent> items,
        MarkdownAsyncNodeRenderer? deferredSceneRenderer = null,
        MarkdownExtensionCallbackLifetime? deferredSceneRendererLifetime = null)
    {
        Items = items;
        DeferredSceneRenderer = deferredSceneRenderer;
        DeferredSceneRendererLifetime = deferredSceneRendererLifetime;
    }

    /// <summary>Gets an empty fragment.</summary>
    public static MarkdownContentFragment Empty { get; } = new(EmptyItems);

    /// <summary>Gets the fragment's top-level content in semantic order.</summary>
    public IReadOnlyList<MarkdownContent> Items { get; }

    // An ordered renderer pipeline stops at the first callback that emits
    // content, so every deferred marker in one fragment has the same producer.
    internal MarkdownAsyncNodeRenderer? DeferredSceneRenderer { get; }
    internal MarkdownExtensionCallbackLifetime? DeferredSceneRendererLifetime { get; }
}

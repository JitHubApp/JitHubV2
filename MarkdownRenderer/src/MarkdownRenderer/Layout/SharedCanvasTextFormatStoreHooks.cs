using System;
using Microsoft.Graphics.Canvas.Text;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Native-custody fault injection for an isolated
/// <see cref="SharedCanvasTextFormatStore"/>. Every callback runs at a fixed
/// construction or publication boundary so tests can prove that failures
/// before, during and after entry creation retire the native format exactly
/// once. Disposal faults belong to the store's retirement owner.
/// </summary>
internal sealed class SharedCanvasTextFormatStoreHooks
{
    /// <summary>Runs after the new format has been initialized from its key.</summary>
    internal Action<CanvasTextFormat>? AfterInitialize { get; init; }

    /// <summary>Runs after the entry has taken custody of the native format.</summary>
    internal Action<SharedCanvasTextFormatEntry>? AfterEntryCreated { get; init; }

    /// <summary>Runs after both references are taken, immediately before the LRU publication.</summary>
    internal Action? BeforeCachePublication { get; init; }

    /// <summary>Fails the cache-miss lease allocation after the entry is created.</summary>
    internal bool FailLeaseConstruction { get; init; }

    /// <summary>Fails the cache-hit lease allocation after its reference is taken.</summary>
    internal bool FailCacheHitLeaseConstruction { get; init; }
}

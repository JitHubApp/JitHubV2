using System;
using Microsoft.Graphics.Canvas.Text;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Native-custody fault injection for an isolated
/// <see cref="SharedCanvasTextFormatStore"/>. Every callback runs at a fixed
/// construction or publication boundary so tests can prove that failures
/// before, during and after entry creation retire the native format exactly
/// once. A store rejects these hooks unless its retirement owner is timerless.
/// </summary>
internal sealed class SharedCanvasTextFormatStoreHooks
{
    /// <summary>Replaces the retirement disposer used for every reserved native format.</summary>
    internal Action<IDisposable>? NativeDisposer { get; init; }

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

    internal bool InjectsFaults =>
        NativeDisposer is not null || AfterInitialize is not null ||
        AfterEntryCreated is not null || BeforeCachePublication is not null ||
        FailLeaseConstruction || FailCacheHitLeaseConstruction;
}

using System;
using MarkdownRenderer.Layout.Boxes;
using Microsoft.Graphics.Canvas;

namespace MarkdownRenderer.Layout;

/// <summary>
/// The caches, budgets and native custody that the documents of one engine
/// share while they are laid out and painted: image caches and admissions,
/// text-format descriptors, paint-session tracking and the retirement owner
/// that retries failed native cleanup.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="MarkdownEngine"/> owns one instance. Engines derived from an
/// engine share their parent's instance; independently built engines,
/// <see cref="MarkdownEngine.Default"/> and each built-in profile engine own
/// their own. Every view that renders an engine's documents therefore stays
/// inside that engine's budgets, and views of different engines never share
/// cached resources.
/// </para>
/// <para>
/// A layout context created outside an engine-driven rebuild gets an
/// isolated instance of its own.
/// </para>
/// </remarks>
internal sealed class MarkdownRenderResources
{
    // Engine-owned trimming outlives no engine: the subscription only holds
    // its owner weakly.
    private readonly ImageMemoryPressureSubscription? _memoryPressure;

    /// <summary>Creates isolated resources with the default budgets.</summary>
    internal MarkdownRenderResources()
        : this(new ImageResourceScope())
    {
    }

    /// <summary>Creates resources over an existing image scope.</summary>
    /// <param name="images">The image caches and admissions to use.</param>
    internal MarkdownRenderResources(ImageResourceScope images)
        : this(images, trimOnMemoryPressure: false)
    {
    }

    private MarkdownRenderResources(ImageResourceScope images, bool trimOnMemoryPressure)
    {
        ArgumentNullException.ThrowIfNull(images);
        Images = images;
        // Create the retry clock before any participating native factory.
        NativeRetirement = new NativeResourceRetirement(createTimer: true);
        TextFormats = new SharedCanvasTextFormatStore(SharedCanvasTextFormatCache.DefaultBudgetBytes, NativeRetirement);
        PaintSessions = new NativePaintSessionResources(NativeRetirement);
        if (trimOnMemoryPressure)
            _memoryPressure = ImageMemoryPressureSubscription.TrySubscribe(images);
    }

    /// <summary>The image caches, budgets and admission gates.</summary>
    internal ImageResourceScope Images { get; }

    /// <summary>Durable custody for native resources whose cleanup failed.</summary>
    internal NativeResourceRetirement NativeRetirement { get; }

    /// <summary>The bounded store of immutable text-format descriptors.</summary>
    internal SharedCanvasTextFormatStore TextFormats { get; }

    /// <summary>The per-drawing-session paint resources.</summary>
    internal NativePaintSessionResources PaintSessions { get; }

    /// <summary>
    /// Creates the resources an engine owns. They also trim their image
    /// caches under system memory pressure.
    /// </summary>
    internal static MarkdownRenderResources CreateForEngine() =>
        new(new ImageResourceScope(), trimOnMemoryPressure: true);

    /// <summary>Drops cached device resources created on <paramref name="device"/>.</summary>
    /// <param name="device">The lost device.</param>
    internal void ReleaseDevice(CanvasDevice device) => Images.ReleaseDevice(device);
}

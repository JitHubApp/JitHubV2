using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using MarkdownRenderer.Document;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Per-build context passed through layout. Provides the canvas device for text
/// metrics, the theme snapshot (pre-resolved, thread-safe), the source map writer,
/// and a block-index counter.
/// </summary>
public sealed class MarkdownLayoutContext
{
    public MarkdownLayoutContext(
        ICanvasResourceCreator resourceCreator,
        ThemeSnapshot themeSnapshot,
        MarkdownSourceMap sourceMap,
        MarkdownExtensionRegistry registry,
        FlowDirection flowDirection,
        DispatcherQueue? dispatcher = null)
    {
        ResourceCreator = resourceCreator;
        ThemeSnapshot = themeSnapshot;
        SourceMap = sourceMap;
        Registry = registry;
        FlowDirection = flowDirection;
        Dispatcher = dispatcher;
    }

    public ICanvasResourceCreator ResourceCreator { get; }
    public ThemeSnapshot ThemeSnapshot { get; }
    public MarkdownSourceMap SourceMap { get; }
    public MarkdownExtensionRegistry Registry { get; }
    public FlowDirection FlowDirection { get; }
    /// <summary>UI-thread dispatcher used to marshal async load completions back
    /// to the thread that owns the canvas. May be null in unit tests.</summary>
    public DispatcherQueue? Dispatcher { get; }

    /// <summary>
    /// Effective rasterization scale of the host control (matches
    /// <c>XamlRoot.RasterizationScale</c>: 1.0 at 100%, 1.5 at 150%, 2.0
    /// at 200%, etc.). Used by raster-fallback image paths (e.g.
    /// <c>SvgSkiaRasterizer</c>) to render at device-pixel resolution so
    /// the result is crisp on high-DPI displays. Defaults to 1.0 — paint
    /// is unchanged at the standard scale and in unit tests.
    /// </summary>
    public double RasterizationScale { get; init; } = 1.0;

    public int NextBlockIndex() => ++_blockIndex;
    private int _blockIndex;

    // ---- Footnote registry ----
    // Records the block indices of each footnote's definition and inline
    // reference so the control can scroll to either end of a back/forward link.
    // Key = footnote order (1-based); value = (defBlockIndex, refBlockIndex).
    // Written by FootnoteRenderer (def) and LayoutBuilder (ref) during build;
    // read by MarkdownRendererControl via LayoutSnapshot after build completes.
    // Concurrent because LayoutBuilder runs on a background thread.
    private readonly ConcurrentDictionary<int, int> _footnoteDefBlocks = new();
    private readonly ConcurrentDictionary<int, int> _footnoteRefBlocks = new();

    /// <summary>Records the block index of the definition for footnote <paramref name="order"/>.</summary>
    public void RegisterFootnoteDef(int order, int blockIndex) => _footnoteDefBlocks[order] = blockIndex;

    /// <summary>Records the block index of the paragraph containing the inline citation for footnote <paramref name="order"/>.</summary>
    public void RegisterFootnoteRef(int order, int blockIndex) => _footnoteRefBlocks[order] = blockIndex;

    /// <summary>Returns block index of the footnote definition, or null.</summary>
    public int? GetFootnoteDefBlock(int order) => _footnoteDefBlocks.TryGetValue(order, out var v) ? v : null;

    /// <summary>Returns block index of the inline citation paragraph, or null.</summary>
    public int? GetFootnoteRefBlock(int order) => _footnoteRefBlocks.TryGetValue(order, out var v) ? v : null;

    /// <summary>Snapshots the registry into plain dictionaries (safe to read off the build thread).</summary>
    public (IReadOnlyDictionary<int,int> Defs, IReadOnlyDictionary<int,int> Refs) SnapshotFootnoteRegistry()
        => (new Dictionary<int, int>(_footnoteDefBlocks), new Dictionary<int, int>(_footnoteRefBlocks));
}

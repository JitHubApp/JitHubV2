using System;
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

    public int NextBlockIndex() => _blockIndex++;
    private int _blockIndex;
}

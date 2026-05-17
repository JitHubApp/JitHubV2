using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Markdig.Extensions.Footnotes;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using MarkdownRenderer.Document;
using MarkdownRenderer.CodeBlocks;
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
    /// <summary>
    /// Initializes a layout context. Advanced renderer authors receive this object
    /// on a background layout thread and must not use it to touch WinUI objects.
    /// </summary>
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

    /// <summary>Canvas resource creator used for text and graphics resources.</summary>
    public ICanvasResourceCreator ResourceCreator { get; }

    /// <summary>Resolved theme snapshot for the current layout pass.</summary>
    public ThemeSnapshot ThemeSnapshot { get; }

    /// <summary>Source map being populated by the current layout pass.</summary>
    public MarkdownSourceMap SourceMap { get; }

    /// <summary>Extension registry used by the current layout pass.</summary>
    public MarkdownExtensionRegistry Registry { get; }

    /// <summary>Flow direction used for text layout.</summary>
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

    /// <summary>Cancellation token for the current background layout pass.</summary>
    public CancellationToken CancellationToken { get; init; }

    /// <summary>True when code block copy buttons should be included in layout metadata.</summary>
    public bool IsCodeBlockCopyEnabled { get; init; } = true;

    /// <summary>Line-number policy for code blocks.</summary>
    public CodeBlockLineNumberMode CodeBlockLineNumberMode { get; init; } = CodeBlockLineNumberMode.AutoMultiline;

    /// <summary>Returns the next one-based block index for a custom block.</summary>
    public int NextBlockIndex() => ++_blockIndex;
    private int _blockIndex;

    private readonly List<string> _styleContextKeys = new();
    private readonly List<string> _styleAliasKeys = new();
    private int _listDepth;

    internal int ListDepth => _listDepth;
    internal int StyleAliasCount => _styleAliasKeys.Count;

    internal IReadOnlyList<string> CreateStyleContextSnapshot()
        => _styleContextKeys.Count == 0 ? Array.Empty<string>() : _styleContextKeys.ToArray();

    internal IReadOnlyList<string> CreateStyleAliasSnapshot()
        => _styleAliasKeys.Count == 0 ? Array.Empty<string>() : _styleAliasKeys.ToArray();

    internal IReadOnlyList<string> CreateStyleAliasSnapshotFrom(int startIndex)
    {
        startIndex = Math.Clamp(startIndex, 0, _styleAliasKeys.Count);
        int count = _styleAliasKeys.Count - startIndex;
        if (count == 0)
            return Array.Empty<string>();

        var result = new string[count];
        _styleAliasKeys.CopyTo(startIndex, result, 0, count);
        return result;
    }

    internal StyleScope PushStyleContext(string key)
    {
        int contextCount = _styleContextKeys.Count;
        int aliasCount = _styleAliasKeys.Count;
        int listDepth = _listDepth;
        if (!string.IsNullOrWhiteSpace(key))
            _styleContextKeys.Add(key);
        return new StyleScope(this, contextCount, aliasCount, listDepth);
    }

    internal StyleScope PushListDepth()
    {
        int contextCount = _styleContextKeys.Count;
        int aliasCount = _styleAliasKeys.Count;
        int listDepth = _listDepth;
        _listDepth++;
        _styleContextKeys.Add(MarkdownElementKeys.ListDepth(_listDepth));
        return new StyleScope(this, contextCount, aliasCount, listDepth);
    }

    internal StyleScope PushMarkdownAttributes(IMarkdownObject markdownObject)
    {
        int contextCount = _styleContextKeys.Count;
        int aliasCount = _styleAliasKeys.Count;
        int listDepth = _listDepth;
        AddMarkdownAttributeAliases(markdownObject, _styleAliasKeys);
        return new StyleScope(this, contextCount, aliasCount, listDepth);
    }

    internal void RegisterMarkdownAttributes(IMarkdownObject markdownObject, int blockIndex)
    {
        var attrs = HtmlAttributesExtensions.TryGetAttributes(markdownObject);
        var id = attrs?.Id;
        if (!string.IsNullOrWhiteSpace(id))
            RegisterFragmentTarget(id, blockIndex);
    }

    internal static IReadOnlyList<string> GetMarkdownAttributeAliases(IMarkdownObject markdownObject)
    {
        var aliases = new List<string>();
        AddMarkdownAttributeAliases(markdownObject, aliases);
        return aliases;
    }

    private static void AddMarkdownAttributeAliases(IMarkdownObject markdownObject, List<string> aliases)
    {
        var attrs = HtmlAttributesExtensions.TryGetAttributes(markdownObject);
        if (attrs is null)
            return;

        if (attrs.Classes is not null)
        {
            foreach (var @class in attrs.Classes)
            {
                if (!string.IsNullOrWhiteSpace(@class))
                    aliases.Add(MarkdownElementKeys.Class(@class));
            }
        }

        if (!string.IsNullOrWhiteSpace(attrs.Id))
            aliases.Add(MarkdownElementKeys.Id(attrs.Id));
    }

    private void RestoreStyleState(int contextCount, int aliasCount, int listDepth)
    {
        if (_styleContextKeys.Count > contextCount)
            _styleContextKeys.RemoveRange(contextCount, _styleContextKeys.Count - contextCount);
        if (_styleAliasKeys.Count > aliasCount)
            _styleAliasKeys.RemoveRange(aliasCount, _styleAliasKeys.Count - aliasCount);
        _listDepth = listDepth;
    }

    internal readonly struct StyleScope : IDisposable
    {
        private readonly MarkdownLayoutContext? _owner;
        private readonly int _contextCount;
        private readonly int _aliasCount;
        private readonly int _listDepth;

        internal StyleScope(MarkdownLayoutContext owner, int contextCount, int aliasCount, int listDepth)
        {
            _owner = owner;
            _contextCount = contextCount;
            _aliasCount = aliasCount;
            _listDepth = listDepth;
        }

        public void Dispose()
            => _owner?.RestoreStyleState(_contextCount, _aliasCount, _listDepth);
    }

    /// <summary>
    /// Throws when a background-layout-only embed callback is invoked from the
    /// UI dispatcher thread. This catches factories that would otherwise touch
    /// WinUI APIs during layout and risk deadlocks or frame stalls.
    /// </summary>
    public void ThrowIfEmbedLayoutCallbackIsOnUiThread(string methodName)
    {
        if (Dispatcher?.HasThreadAccess != true)
            return;

        throw new InvalidOperationException(
            $"IMarkdownEmbedFactory.{methodName} must run on the background layout thread and must not touch WinUI APIs. " +
            "Move WinUI work to CreateBlock or RecycleBlock.");
    }

    // ---- Footnote registry ----
    // Records the block indices of each footnote's definition and inline
    // reference so the control can scroll to either end of a back/forward link.
    // Key = footnote order (1-based); value = (defBlockIndex, refBlockIndex).
    // Written by FootnoteRenderer (def) and LayoutBuilder (ref) during build;
    // read by MarkdownRendererControl via LayoutSnapshot after build completes.
    // Concurrent because LayoutBuilder runs on a background thread.
    private readonly ConcurrentDictionary<int, int> _footnoteDefBlocks = new();
    private readonly ConcurrentDictionary<int, int> _footnoteRefBlocks = new();
    private readonly ConcurrentDictionary<string, int> _fragmentTargetBlocks = new(StringComparer.Ordinal);
    private readonly Dictionary<Footnote, int> _footnoteOrders = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<int> _reservedFootnoteOrders = new();
    private readonly object _footnoteOrderGate = new();
    private int _nextGeneratedFootnoteOrder;

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

    /// <summary>
    /// Returns one stable display/navigation order for a Markdig footnote during
    /// the current layout pass. Markdig normally assigns <see cref="Footnote.Order"/>,
    /// but malformed or synthetic footnotes can arrive without one; generated
    /// fallback values are shared by references and definitions.
    /// </summary>
    public int GetOrCreateFootnoteOrder(Footnote footnote, int fallbackHint = 0)
    {
        if (footnote is null)
            return Math.Max(1, fallbackHint);

        lock (_footnoteOrderGate)
        {
            if (_footnoteOrders.TryGetValue(footnote, out var existing))
                return existing;

            int order = footnote.Order > 0 ? footnote.Order : fallbackHint;
            if (order <= 0 || _reservedFootnoteOrders.Contains(order))
            {
                do { _nextGeneratedFootnoteOrder++; }
                while (_reservedFootnoteOrders.Contains(_nextGeneratedFootnoteOrder));
                order = _nextGeneratedFootnoteOrder;
            }

            _reservedFootnoteOrders.Add(order);
            _footnoteOrders[footnote] = order;
            return order;
        }
    }

    internal void RegisterFragmentTarget(string id, int blockIndex)
    {
        id = NormalizeFragmentId(id);
        if (id.Length > 0)
            _fragmentTargetBlocks[id] = blockIndex;
    }

    internal IReadOnlyDictionary<string, int> SnapshotFragmentTargets()
        => new Dictionary<string, int>(_fragmentTargetBlocks, StringComparer.Ordinal);

    private static string NormalizeFragmentId(string id)
    {
        id = id.Trim();
        if (id.StartsWith("#", StringComparison.Ordinal))
            id = id.Substring(1);
        return id;
    }
}

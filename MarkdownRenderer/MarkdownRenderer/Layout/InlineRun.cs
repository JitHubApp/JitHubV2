using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Parsing;

namespace MarkdownRenderer.Layout;

/// <summary>
/// One inline segment inside a paragraph or heading. Inline runs do not lay
/// themselves out individually; the parent block joins them into a single
/// CanvasTextLayout with per-range style spans.
/// </summary>
internal abstract class InlineRun
{
    protected InlineRun()
    {
    }

    // Copies logical/style metadata only; native layout and ownership stay
    // generation-local.
    protected InlineRun(InlineRun source)
    {
        InlineIndex = source.InlineIndex;
        RenderedLength = source.RenderedLength;
        SourceSpan = source.SourceSpan;
        ElementKey = source.ElementKey;
        StyleAliases = CopyStrings(source.StyleAliases);
        StyleModifierKeys = CopyStrings(source.StyleModifierKeys);
        SemanticHeadingKey = source.SemanticHeadingKey;
    }

    private static string[] CopyStrings(IReadOnlyList<string> source)
    {
        var copy = new string[source.Count];
        for (int index = 0; index < copy.Length; index++)
            copy[index] = source[index];
        return copy;
    }

    public int InlineIndex { get; internal set; }
    public int RenderedLength { get; protected set; }
    public SourceSpan SourceSpan { get; internal set; }
    /// <summary>
    /// Empty string means "inherit container's style". Set to a specific key
    /// (e.g. <see cref="MarkdownElementKeys.Strong"/>) to apply a delta override.
    /// </summary>
    public string ElementKey { get; init; } = string.Empty;

    /// <summary>
    /// Attribute-derived style aliases (for example <c>.warning</c> or
    /// <c>#intro</c>) captured from the layout context when this run is built.
    /// </summary>
    public IReadOnlyList<string> StyleAliases { get; internal set; } = Array.Empty<string>();

    /// <summary>
    /// Inline element styles inherited from a formatting container that had to
    /// be split into multiple runs (for example, <c>**before [link](...) after**</c>).
    /// Keeping these modifiers separate preserves the interactive link run while
    /// retaining the surrounding emphasis.
    /// </summary>
    internal IReadOnlyList<string> StyleModifierKeys { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Heading style/semantics inherited from safe HTML that is flattened into
    /// an inline-only host such as a table cell. Ordinary Markdown headings
    /// remain block containers and leave this empty.
    /// </summary>
    internal string SemanticHeadingKey { get; set; } = string.Empty;

    public void SetStyleAliases(IReadOnlyList<string> styleAliases)
        => StyleAliases = styleAliases ?? Array.Empty<string>();

    internal bool HasStyleModifier(string elementKey)
    {
        foreach (string key in StyleModifierKeys)
        {
            if (string.Equals(key, elementKey, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>The text contributed by this run to the inline buffer.</summary>
    public abstract string Text { get; }

    /// <summary>
    /// Logical UTF-16 length without requiring a direct-source literal to
    /// allocate its string representation.
    /// </summary>
    internal int TextLength => this is SourceSliceTextRun sourceSlice
        ? sourceSlice.SliceLength
        : Text.Length;

    /// <summary>Length of the native-layout representation without materializing a direct literal.</summary>
    internal int LayoutTextLength => this is SourceSliceTextRun sourceSlice
        ? sourceSlice.SliceLength
        : LayoutText.Length;

    internal void AppendLayoutTextTo(StringBuilder builder)
    {
        if (this is SourceSliceTextRun sourceSlice)
            sourceSlice.AppendLayoutTextTo(builder);
        else
            builder.Append(LayoutText);
    }

    /// <summary>
    /// The same-length text used only by the native text layout. Most runs use
    /// their semantic text directly; a standalone safe-HTML line break uses a
    /// blank layout character so DirectWrite retains one empty line box rather
    /// than creating an extra trailing line for the terminal newline.
    /// </summary>
    internal virtual string LayoutText => Text;

    /// <summary>
    /// Text exposed through UI Automation. Most runs expose their rendered text;
    /// visual atomic runs such as images can expose richer fallback text.
    /// </summary>
    public virtual string AccessibleText => Text;
}

internal class TextRun : InlineRun
{
    // Keep the ordinary TextRun field shape compact. Direct-source subclasses
    // publish their optional string cache into this same slot when a caller
    // explicitly asks for Text.
    private string _text;
    public TextRun(string text) { _text = text ?? string.Empty; RenderedLength = _text.Length; }
    protected TextRun(TextRun source) : base(source) => _text = Volatile.Read(ref source._text);
    internal TextRun CloneForPrivateRasterPlacement() => new(this);

    protected string? ReadMaterializedText()
    {
        string value = Volatile.Read(ref _text);
        return value.Length == 0 ? null : value;
    }

    protected string PublishMaterializedText(string value)
    {
        string existing = Interlocked.CompareExchange(ref _text, value, string.Empty);
        return existing.Length == 0 ? value : existing;
    }

    public override string Text => _text;
}

/// <summary>
/// A literal run whose rendered UTF-16 content is exactly one range of the
/// immutable Markdown source. Its string-facing API stays compatible, while
/// layout and semantic construction can consume the source range directly.
/// </summary>
internal sealed class SourceSliceTextRun : TextRun
{
    private readonly string _sourceText;
    private readonly int _sourceStart;

    internal SourceSliceTextRun(string sourceText, int sourceStart, int length)
        : base(string.Empty)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        if (length <= 0 || sourceStart < 0 || sourceStart > sourceText.Length - length)
            throw new ArgumentOutOfRangeException(nameof(length));

        _sourceText = sourceText;
        _sourceStart = sourceStart;
        RenderedLength = length;
    }

    private SourceSliceTextRun(SourceSliceTextRun source) : base(source)
    {
        _sourceText = source._sourceText;
        _sourceStart = source._sourceStart;
    }

    internal string SourceText => _sourceText;
    internal int SourceStart => _sourceStart;
    internal int SliceLength => RenderedLength;

    internal new SourceSliceTextRun CloneForPrivateRasterPlacement() => new(this);

    public override string Text
    {
        get
        {
            string? cached = ReadMaterializedText();
            if (cached is not null)
                return cached;

            string materialized = _sourceText.Substring(_sourceStart, RenderedLength);
            return PublishMaterializedText(materialized);
        }
    }

    internal new void AppendLayoutTextTo(StringBuilder builder)
        => builder.Append(_sourceText, _sourceStart, RenderedLength);
}

internal sealed class CodeInlineRun : InlineRun
{
    private readonly string _text;
    public CodeInlineRun(string text)
    {
        _text = text ?? string.Empty;
        RenderedLength = _text.Length;
        ElementKey = MarkdownElementKeys.CodeInline;
    }
    private CodeInlineRun(CodeInlineRun source) : base(source) => _text = source._text;
    internal CodeInlineRun CloneForPrivateRasterPlacement() => new(this);
    public override string Text => _text;
}

internal sealed class StrongRun : InlineRun
{
    private readonly string _text;
    public StrongRun(string text)
    {
        _text = text ?? string.Empty;
        RenderedLength = _text.Length;
        ElementKey = MarkdownElementKeys.Strong;
    }
    private StrongRun(StrongRun source) : base(source) => _text = source._text;
    internal StrongRun CloneForPrivateRasterPlacement() => new(this);
    public override string Text => _text;
}

internal sealed class EmphasisRun : InlineRun
{
    private readonly string _text;
    public EmphasisRun(string text)
    {
        _text = text ?? string.Empty;
        RenderedLength = _text.Length;
        ElementKey = MarkdownElementKeys.Emphasis;
    }
    private EmphasisRun(EmphasisRun source) : base(source) => _text = source._text;
    internal EmphasisRun CloneForPrivateRasterPlacement() => new(this);
    public override string Text => _text;
}

internal sealed class StrikethroughRun : InlineRun
{
    private readonly string _text;
    public StrikethroughRun(string text)
    {
        _text = text ?? string.Empty;
        RenderedLength = _text.Length;
        ElementKey = MarkdownElementKeys.Strikethrough;
    }
    private StrikethroughRun(StrikethroughRun source) : base(source) => _text = source._text;
    internal StrikethroughRun CloneForPrivateRasterPlacement() => new(this);
    public override string Text => _text;
}

internal sealed class SubscriptRun : InlineRun
{
    private readonly string _text;
    public SubscriptRun(string text)
    {
        _text = text ?? string.Empty;
        RenderedLength = _text.Length;
        ElementKey = MarkdownElementKeys.Subscript;
    }
    private SubscriptRun(SubscriptRun source) : base(source) => _text = source._text;
    internal SubscriptRun CloneForPrivateRasterPlacement() => new(this);
    public override string Text => _text;
}

internal sealed class SuperscriptRun : InlineRun
{
    private readonly string _text;
    public SuperscriptRun(string text)
    {
        _text = text ?? string.Empty;
        RenderedLength = _text.Length;
        ElementKey = MarkdownElementKeys.Superscript;
    }
    private SuperscriptRun(SuperscriptRun source) : base(source) => _text = source._text;
    internal SuperscriptRun CloneForPrivateRasterPlacement() => new(this);
    public override string Text => _text;
}

internal sealed class InsertedRun : InlineRun
{
    private readonly string _text;
    public InsertedRun(string text)
    {
        _text = text ?? string.Empty;
        RenderedLength = _text.Length;
        ElementKey = MarkdownElementKeys.Inserted;
    }
    private InsertedRun(InsertedRun source) : base(source) => _text = source._text;
    internal InsertedRun CloneForPrivateRasterPlacement() => new(this);
    public override string Text => _text;
}

internal sealed class MarkedRun : InlineRun
{
    private readonly string _text;
    public MarkedRun(string text)
    {
        _text = text ?? string.Empty;
        RenderedLength = _text.Length;
        ElementKey = MarkdownElementKeys.Marked;
    }
    private MarkedRun(MarkedRun source) : base(source) => _text = source._text;
    internal MarkedRun CloneForPrivateRasterPlacement() => new(this);
    public override string Text => _text;
}

internal sealed class AbbreviationRun : InlineRun
{
    private readonly string _text;
    public readonly string Expansion;

    public AbbreviationRun(string text, string expansion)
    {
        _text = text ?? string.Empty;
        Expansion = expansion ?? string.Empty;
        RenderedLength = _text.Length;
        ElementKey = MarkdownElementKeys.Abbreviation;
    }

    private AbbreviationRun(AbbreviationRun source) : base(source)
    {
        _text = source._text;
        Expansion = source.Expansion;
    }

    internal AbbreviationRun CloneForPrivateRasterPlacement() => new(this);

    public override string Text => _text;

    public override string AccessibleText =>
        string.IsNullOrWhiteSpace(Expansion) ? _text : $"{_text} ({Expansion})";
}

internal sealed class LinkRun : InlineRun
{
    private readonly string _text;
    public readonly string Url;
    public readonly string? Title;
    public string? AccessibilityName { get; init; }
    public string? DisclosureId { get; init; }
    public bool IsDisclosure => !string.IsNullOrEmpty(DisclosureId);
    public bool IsExpanded { get; init; }
    /// <summary>
    /// When true, the run is rendered at a reduced size and raised baseline
    /// (like a superscript). Used for footnote citation markers [^1].
    /// </summary>
    public bool IsSuperscript { get; internal set; }
    public LinkRun(string text, string url, string? title = null)
    {
        _text = text ?? string.Empty;
        Url = url ?? string.Empty;
        Title = title;
        RenderedLength = _text.Length;
        ElementKey = MarkdownElementKeys.Link;
    }

    private LinkRun(LinkRun source)
        : base(source)
    {
        _text = source._text;
        Url = source.Url;
        Title = source.Title;
        AccessibilityName = source.AccessibilityName;
        DisclosureId = source.DisclosureId;
        IsExpanded = source.IsExpanded;
        IsSuperscript = source.IsSuperscript;
    }

    internal LinkRun CloneForPrivateRasterPlacement()
    {
        if (IsDisclosure || IsExpanded)
            throw new InvalidOperationException("Stateful disclosure links cannot be cloned for raster placement.");
        return new LinkRun(this);
    }

    public override string Text => _text;
    public override string AccessibleText => string.IsNullOrWhiteSpace(AccessibilityName)
        ? _text
        : AccessibilityName;
}

internal sealed class InlineImageRun : InlineRun
{
    public readonly string Url;
    public readonly string? Title;
    public readonly string? LinkUrl;
    public readonly string? LinkTitle;
    public bool IsLinked => !string.IsNullOrWhiteSpace(LinkUrl);
    public readonly string AltText;
    public readonly Boxes.ImageBox Image;
    internal float DesiredWidth { get; private set; }
    internal float DesiredHeight { get; private set; }

    public InlineImageRun(
        MarkdownLayoutContext context,
        string altText,
        string url,
        string? title = null,
        string? linkUrl = null,
        string? linkTitle = null,
        SafeHtmlLength? requestedWidth = null,
        SafeHtmlLength? requestedHeight = null)
    {
        AltText = altText ?? string.Empty;
        Url = url ?? string.Empty;
        Title = title;
        LinkUrl = linkUrl;
        LinkTitle = linkTitle;
        Image = new Boxes.ImageBox(context, Url, AltText, requestedWidth, requestedHeight)
        {
            Margin = default
        };
        DesiredWidth = 24f;
        DesiredHeight = 24f;
        RenderedLength = 1;
    }

    private InlineImageRun(InlineImageRun source, Boxes.ImageBox image)
        : base(source)
    {
        Url = source.Url;
        Title = source.Title;
        LinkUrl = source.LinkUrl;
        LinkTitle = source.LinkTitle;
        AltText = source.AltText;
        Image = image;
        DesiredWidth = source.DesiredWidth;
        DesiredHeight = source.DesiredHeight;
    }

    internal bool TryClassifyPrivateImagePlacement(
        MarkdownLayoutContext targetContext,
        bool allowIdleImagePlacements,
        out bool isIdleDescriptor,
        out int expectedIdleLoadDemandGeneration) =>
        Image.TryClassifyPrivateImagePlacement(
            targetContext,
            allowIdleImagePlacements,
            out isIdleDescriptor,
            out expectedIdleLoadDemandGeneration);

    internal bool TryCreatePrivateImagePlacementClone(
        MarkdownLayoutContext targetContext,
        out InlineImageRun? placement,
        out Boxes.ImageBox? expectedSourceImage,
        out Boxes.SharedCanvasBitmapCache.Lease? expectedSourceLease,
        out Boxes.ImageBox.CompletedSvgPlacementMetadataFence? svgMetadataFence,
        out bool isIdleDescriptor,
        out int expectedIdleLoadDemandGeneration)
    {
        placement = null;
        expectedSourceImage = null;
        expectedSourceLease = null;
        svgMetadataFence = null;
        isIdleDescriptor = false;
        expectedIdleLoadDemandGeneration = 0;

        if (!Image.TryCreatePrivateImagePlacementClone(
                targetContext,
                out Boxes.ImageBox? clonedImage,
                out expectedSourceLease,
                out svgMetadataFence,
                out isIdleDescriptor,
                out expectedIdleLoadDemandGeneration) ||
            clonedImage is null)
        {
            expectedSourceLease = null;
            svgMetadataFence = null;
            isIdleDescriptor = false;
            expectedIdleLoadDemandGeneration = 0;
            return false;
        }

        try
        {
            placement = new InlineImageRun(this, clonedImage);
            expectedSourceImage = Image;
            return true;
        }
        catch (Exception constructionFailure)
        {
            try
            {
                targetContext.DiscardConstructionBlock(clonedImage);
            }
            catch (Exception cleanupFailure)
            {
                try
                {
                    constructionFailure.Data["MarkdownRenderer.PrivateImageRunCleanupFailure"] = cleanupFailure;
                }
                catch
                {
                    // Cleanup diagnostics must not mask the primary failure.
                }
            }
            expectedSourceImage = null;
            expectedSourceLease = null;
            svgMetadataFence = null;
            isIdleDescriptor = false;
            expectedIdleLoadDemandGeneration = 0;
            throw;
        }
    }

    internal bool TryCreatePrivateRasterPlacementClone(
        MarkdownLayoutContext targetContext,
        out InlineImageRun? placement,
        out Boxes.ImageBox? expectedSourceImage,
        out Boxes.SharedCanvasBitmapCache.Lease? expectedSourceLease,
        out Boxes.ImageBox.CompletedSvgPlacementMetadataFence? svgMetadata)
    {
        placement = null;
        expectedSourceImage = null;
        expectedSourceLease = null;
        svgMetadata = null;
        if (!Image.TryCreateCompletedRasterPlacementClone(
                targetContext,
                out Boxes.ImageBox? clonedImage,
                out expectedSourceLease,
                out svgMetadata) ||
            clonedImage is null)
            return false;

        try
        {
            placement = new InlineImageRun(this, clonedImage);
            expectedSourceImage = Image;
            return true;
        }
        catch (Exception constructionFailure)
        {
            try
            {
                targetContext.DiscardConstructionBlock(clonedImage);
            }
            catch (Exception cleanupFailure)
            {
                try
                {
                    constructionFailure.Data["MarkdownRenderer.PrivateRasterRunCleanupFailure"] = cleanupFailure;
                }
                catch
                {
                    // Cleanup diagnostics must not mask the primary construction failure.
                }
            }
            expectedSourceLease = null;
            svgMetadata = null;
            throw;
        }
    }

    public override string Text => InlineEmbedRun.PlaceholderChar;

    public override string AccessibleText => AltText;

    internal void Measure(
        float maxWidth,
        float lineHeight,
        Boxes.ImageBox.InlineMeasureCheckpoint? checkpoint = null)
    {
        var size = checkpoint is { } transaction
            ? Image.MeasureInline(maxWidth, lineHeight, transaction)
            : Image.MeasureInline(maxWidth, lineHeight);
        DesiredWidth = Math.Max(1f, (float)size.Width);
        DesiredHeight = Math.Max(1f, (float)size.Height);
    }

    internal void RestoreMeasuredSize(float width, float height)
    {
        DesiredWidth = width;
        DesiredHeight = height;
    }
}

internal sealed class LineBreakRun : InlineRun
{
    private readonly string _text;

    /// <summary>
    /// Whether this is the only run in a safe-HTML block. Its semantic text
    /// remains a newline, but its layout representation is a blank character
    /// occupying exactly one line box.
    /// </summary>
    internal bool UseSingleLineLayout { get; set; }

    public LineBreakRun(bool isHard)
    {
        _text = isHard ? "\n" : " ";
        RenderedLength = 1;
    }

    private LineBreakRun(LineBreakRun source) : base(source)
    {
        _text = source._text;
        UseSingleLineLayout = source.UseSingleLineLayout;
    }

    internal LineBreakRun CloneForPrivateRasterPlacement() => new(this);

    public override string Text => _text;

    internal override string LayoutText =>
        UseSingleLineLayout && _text == "\n" ? " " : _text;
}

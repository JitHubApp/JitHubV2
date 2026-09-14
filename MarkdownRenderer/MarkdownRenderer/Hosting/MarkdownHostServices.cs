using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Images;
using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace MarkdownRenderer.Hosting;

/// <summary>
/// Stable image-resolution service. It deliberately inherits the 0.x contract
/// so existing controls and resolvers remain source compatible.
/// </summary>
public interface IImageResolver : IMarkdownImageResolver
{
}

/// <summary>
/// Stable asynchronous, cancellation-aware syntax-highlighting service.
/// Implementations and captured state must be thread-safe. A renderer can
/// query <see cref="ICodeBlockSyntaxHighlighter.Revision"/> and invoke
/// <see cref="HighlightAsync(CodeBlockHighlightRequest, CancellationToken)"/>
/// concurrently for different blocks and controls. Implementations must not
/// require a UI synchronization context.
/// </summary>
#pragma warning disable CS0618 // The stable service intentionally bridges the 0.x compatibility interface.
public interface ICodeHighlighter : ICodeBlockSyntaxHighlighter
#pragma warning restore CS0618
{
    /// <summary>Highlights one code block without blocking the UI thread.</summary>
    ValueTask<CodeBlockHighlightResult?> HighlightAsync(
        CodeBlockHighlightRequest request,
        CancellationToken cancellationToken);

    ValueTask<CodeBlockHighlightResult?> ICodeBlockSyntaxHighlighter.HighlightAsync(
        CodeBlockHighlightRequest request)
        => HighlightAsync(request, request.CancellationToken);
}

/// <summary>Supplies localized renderer strings without exposing resource internals.</summary>
public interface IMarkdownStringProvider
{
    /// <summary>
    /// Returns a localized value for a stable renderer key, or null to use the
    /// library fallback. Implementations must be thread-safe.
    /// </summary>
    string? GetString(string key, CultureInfo culture);
}

/// <summary>Stable localization keys supplied to <see cref="IMarkdownStringProvider"/>.</summary>
public static class MarkdownStringKeys
{
    /// <summary>Document automation name.</summary>
    public const string DocumentName = "Document.Name";
    /// <summary>Generic copy command.</summary>
    public const string Copy = "Command.Copy";
    /// <summary>Copy exact markdown source.</summary>
    public const string CopyMarkdown = "Command.CopyMarkdown";
    /// <summary>Copy a link target.</summary>
    public const string CopyLink = "Command.CopyLink";
    /// <summary>Copy an image.</summary>
    public const string CopyImage = "Command.CopyImage";
    /// <summary>Copy a table.</summary>
    public const string CopyTable = "Command.CopyTable";
    /// <summary>Select-all command.</summary>
    public const string SelectAll = "Command.SelectAll";
    /// <summary>Accessible name for the touch selection start handle.</summary>
    public const string SelectionStartHandle = "Selection.StartHandle";
    /// <summary>Accessible name for the touch selection end handle.</summary>
    public const string SelectionEndHandle = "Selection.EndHandle";
    /// <summary>Keyboard guidance for touch selection handles.</summary>
    public const string SelectionHandleHelp = "Selection.HandleHelp";
    /// <summary>Code-block copy command and automation name.</summary>
    public const string CopyCode = "Command.CopyCode";
    /// <summary>Transient code-copied feedback.</summary>
    public const string CodeCopied = "Feedback.CodeCopied";
    /// <summary>Image loading announcement format. Placeholder <c>{0}</c> is the image description.</summary>
    public const string ImageLoading = "Image.Loading";
    /// <summary>Image error announcement format. Placeholder <c>{0}</c> is the image description.</summary>
    public const string ImageError = "Image.Error";
    /// <summary>Accessible name for a completed read-only task marker.</summary>
    public const string TaskCompleted = "Task.Completed";
    /// <summary>Accessible name for an incomplete read-only task marker.</summary>
    public const string TaskIncomplete = "Task.Incomplete";
    /// <summary>Help text for a read-only task marker.</summary>
    public const string TaskReadOnly = "Task.ReadOnly";
    /// <summary>Help text for an editable task marker.</summary>
    public const string TaskToggle = "Task.Toggle";
    /// <summary>Default accessible name for an image without usable alternative text.</summary>
    public const string ImageName = "Image.Name";
    /// <summary>Default accessible name for a table.</summary>
    public const string TableName = "Table.Name";
    /// <summary>Default accessible name for a list.</summary>
    public const string ListName = "List.Name";
    /// <summary>Default accessible name for embedded content.</summary>
    public const string EmbeddedContentName = "EmbeddedContent.Name";
    /// <summary>Default accessible name for a diagram.</summary>
    public const string DiagramName = "Diagram.Name";
    /// <summary>Code-language help-text format. Placeholder <c>{0}</c> is the language name.</summary>
    public const string CodeLanguageHelp = "Code.LanguageHelp";
    /// <summary>Default label for a safe-HTML disclosure without a summary.</summary>
    public const string HtmlDetails = "Html.Details";
    /// <summary>Notice shown when safe-HTML input exceeds a configured budget.</summary>
    public const string HtmlBudgetExceeded = "Html.BudgetExceeded";
    /// <summary>Text-pattern style name for normal body content.</summary>
    public const string StyleBody = "Style.Body";
    /// <summary>Text-pattern style name for level-one headings.</summary>
    public const string StyleHeading1 = "Style.Heading1";
    /// <summary>Text-pattern style name for level-two headings.</summary>
    public const string StyleHeading2 = "Style.Heading2";
    /// <summary>Text-pattern style name for level-three headings.</summary>
    public const string StyleHeading3 = "Style.Heading3";
    /// <summary>Text-pattern style name for level-four headings.</summary>
    public const string StyleHeading4 = "Style.Heading4";
    /// <summary>Text-pattern style name for level-five headings.</summary>
    public const string StyleHeading5 = "Style.Heading5";
    /// <summary>Text-pattern style name for level-six headings.</summary>
    public const string StyleHeading6 = "Style.Heading6";
    /// <summary>Text-pattern style name for fenced code blocks.</summary>
    public const string StyleCodeBlock = "Style.CodeBlock";
    /// <summary>Text-pattern style name for inline code.</summary>
    public const string StyleInlineCode = "Style.InlineCode";
    /// <summary>Text-pattern style name for quotations.</summary>
    public const string StyleQuote = "Style.Quote";
    /// <summary>Text-pattern style name for links.</summary>
    public const string StyleLink = "Style.Link";
    /// <summary>Text-pattern style name for strong text.</summary>
    public const string StyleStrong = "Style.Strong";
    /// <summary>Text-pattern style name for emphasized text.</summary>
    public const string StyleEmphasis = "Style.Emphasis";
    /// <summary>Text-pattern style name for struck text.</summary>
    public const string StyleStrikethrough = "Style.Strikethrough";
    /// <summary>Text-pattern style name for subscript text.</summary>
    public const string StyleSubscript = "Style.Subscript";
    /// <summary>Text-pattern style name for superscript text.</summary>
    public const string StyleSuperscript = "Style.Superscript";
    /// <summary>Text-pattern style name for inserted text.</summary>
    public const string StyleInserted = "Style.Inserted";
    /// <summary>Text-pattern style name for marked text.</summary>
    public const string StyleMarked = "Style.Marked";
    /// <summary>Text-pattern style name for abbreviations.</summary>
    public const string StyleAbbreviation = "Style.Abbreviation";
    /// <summary>Text-pattern style name for definition terms.</summary>
    public const string StyleDefinitionTerm = "Style.DefinitionTerm";
    /// <summary>Text-pattern style name for definition descriptions.</summary>
    public const string StyleDefinitionDescription = "Style.DefinitionDescription";
    /// <summary>Text-pattern style name for figures.</summary>
    public const string StyleFigure = "Style.Figure";
    /// <summary>Text-pattern style name for figure captions.</summary>
    public const string StyleFigureCaption = "Style.FigureCaption";
    /// <summary>Text-pattern style name for diagrams.</summary>
    public const string StyleDiagram = "Style.Diagram";
    /// <summary>Text-pattern style name for list markers.</summary>
    public const string StyleListMarker = "Style.ListMarker";
    /// <summary>Text-pattern style name for tables.</summary>
    public const string StyleTable = "Style.Table";
    /// <summary>Text-pattern style name for table headers.</summary>
    public const string StyleTableHeader = "Style.TableHeader";
    /// <summary>Text-pattern style name for table cells.</summary>
    public const string StyleTableCell = "Style.TableCell";
}

/// <summary>Stable identifiers for target-aware markdown commands.</summary>
public enum MarkdownCommandKind
{
    /// <summary>Copy rendered plain text and rich HTML.</summary>
    CopyRendered,
    /// <summary>Copy exact markdown source.</summary>
    CopyMarkdown,
    /// <summary>Copy a link target.</summary>
    CopyLink,
    /// <summary>Copy an image.</summary>
    CopyImage,
    /// <summary>Copy code without its fence.</summary>
    CopyCode,
    /// <summary>Copy a table in a host-selected format.</summary>
    CopyTable,
    /// <summary>Activate a declarative action.</summary>
    Activate,
    /// <summary>Toggle editable task-list state.</summary>
    ToggleTask,
}

/// <summary>Immutable context used to resolve a host command.</summary>
public sealed class MarkdownCommandContext
{
    /// <summary>Initializes command context.</summary>
    public MarkdownCommandContext(
        MarkdownCommandKind kind,
        SourceSpan sourceRange,
        string? target = null,
        string? language = null)
    {
        Kind = kind;
        SourceRange = sourceRange;
        Target = string.IsNullOrWhiteSpace(target) ? null : target;
        Language = string.IsNullOrWhiteSpace(language) ? null : language.Trim();
    }

    /// <summary>Gets the requested command kind.</summary>
    public MarkdownCommandKind Kind { get; }
    /// <summary>Gets the half-open UTF-16 source range targeted by the command.</summary>
    public SourceSpan SourceRange { get; }
    /// <summary>Gets an optional URI, action, image source, or other target.</summary>
    public string? Target { get; }
    /// <summary>Gets an optional normalized code language.</summary>
    public string? Language { get; }
}

/// <summary>Resolves host-owned commands for markdown targets.</summary>
public interface IMarkdownCommandProvider
{
    /// <summary>Returns a command for the supplied target, or null for library behavior.</summary>
    ICommand? GetCommand(MarkdownCommandContext context);
}

/// <summary>Immutable request for a viewport-aware hosted element.</summary>
public sealed class MarkdownHostedElementRequest
{
    private static readonly IReadOnlyDictionary<string, string> EmptyAttributes =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());

    /// <summary>Initializes a hosted-element request.</summary>
    public MarkdownHostedElementRequest(
        string factoryKey,
        SourceSpan sourceRange,
        Rect layoutBounds,
        Rect effectiveViewport,
        IReadOnlyDictionary<string, string>? attributes = null,
        MarkdownAccessibilityRole accessibilityRole = MarkdownAccessibilityRole.Group,
        string? accessibilityName = null,
        string? accessibilityDescription = null,
        string? semanticText = null,
        string? automationId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(factoryKey);
        FactoryKey = factoryKey.Trim();
        SourceRange = sourceRange;
        LayoutBounds = layoutBounds;
        EffectiveViewport = effectiveViewport;
        Attributes = CopyAttributes(attributes);
        AccessibilityRole = accessibilityRole;
        AccessibilityName = NormalizeOptional(accessibilityName);
        AccessibilityDescription = NormalizeOptional(accessibilityDescription);
        SemanticText = string.IsNullOrWhiteSpace(semanticText) ? null : semanticText;
        AutomationId = NormalizeOptional(automationId);
    }

    /// <summary>Gets the stable factory registration key.</summary>
    public string FactoryKey { get; }
    /// <summary>Gets the half-open UTF-16 source range.</summary>
    public SourceSpan SourceRange { get; }
    /// <summary>Gets the element's document-coordinate layout bounds.</summary>
    public Rect LayoutBounds { get; }
    /// <summary>Gets the current effective viewport in document coordinates.</summary>
    public Rect EffectiveViewport { get; }
    /// <summary>Gets immutable declarative attributes emitted by the extension.</summary>
    public IReadOnlyDictionary<string, string> Attributes { get; }
    /// <summary>Gets the requested parser-independent accessibility role.</summary>
    public MarkdownAccessibilityRole AccessibilityRole { get; }
    /// <summary>Gets the requested accessible name.</summary>
    public string? AccessibilityName { get; }
    /// <summary>Gets the requested accessible description/help text.</summary>
    public string? AccessibilityDescription { get; }
    /// <summary>Gets the text contributed to document text and copy operations.</summary>
    public string? SemanticText { get; }
    /// <summary>Gets the renderer-generated stable identity for the logical element.</summary>
    public string? AutomationId { get; }

    private static IReadOnlyDictionary<string, string> CopyAttributes(
        IReadOnlyDictionary<string, string>? attributes)
    {
        if (attributes is null || attributes.Count == 0)
            return EmptyAttributes;

        var copy = new Dictionary<string, string>(attributes.Count, StringComparer.Ordinal);
        foreach (var pair in attributes)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pair.Key);
            copy.Add(pair.Key, pair.Value ?? string.Empty);
        }

        return new ReadOnlyDictionary<string, string>(copy);
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// Creates WinUI elements only for declarative hosted-content requests that
/// are near the effective viewport.
/// </summary>
public interface IMarkdownHostedElementFactory
{
    /// <summary>Creates a hosted element on the UI thread.</summary>
    ValueTask<FrameworkElement?> CreateAsync(
        MarkdownHostedElementRequest request,
        CancellationToken cancellationToken);

    /// <summary>Releases host-owned state when an element is virtualized away.</summary>
    void Recycle(MarkdownHostedElementRequest request, FrameworkElement element)
    {
    }
}

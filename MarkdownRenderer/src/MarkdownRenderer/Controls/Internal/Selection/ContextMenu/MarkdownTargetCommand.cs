using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Windows.Input;
using MarkdownRenderer.Hosting;

namespace MarkdownRenderer.Controls.Internal.Selection.ContextMenu;

/// <summary>
/// Immutable description of a context action. The descriptor contains only
/// stable public command data; live layout objects remain private to the
/// control that owns them.
/// </summary>
internal readonly struct MarkdownTargetCommand
{
    public MarkdownTargetCommand(
        MarkdownCommandKind Kind,
        SourceSpan SourceRange,
        string? Target = null,
        string? Language = null)
    {
        this.Kind = Kind;
        this.SourceRange = SourceRange;
        this.Target = Target;
        this.Language = Language;
    }

    public readonly MarkdownCommandKind Kind;

    public readonly SourceSpan SourceRange;

    public readonly string? Target;

    public readonly string? Language;

    internal MarkdownCommandContext CreateContext()
        => new(Kind, SourceRange, Target, Language);
}

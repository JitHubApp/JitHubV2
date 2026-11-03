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

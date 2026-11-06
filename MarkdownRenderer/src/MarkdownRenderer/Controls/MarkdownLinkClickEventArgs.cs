using System;

namespace MarkdownRenderer.Controls;

/// <summary>Event data for markdown link activation.</summary>
public sealed class MarkdownLinkClickEventArgs : EventArgs
{
    /// <summary>Initializes link activation event data.</summary>
    public MarkdownLinkClickEventArgs(string url, string? title)
        : this(
            url,
            title,
            SourceSpan.Empty,
            MarkdownLinkInputKind.Programmatic,
            MarkdownInputModifiers.None,
            MarkdownLinkDisposition.CurrentView)
    {
    }

    /// <summary>Initializes complete link activation event data.</summary>
    public MarkdownLinkClickEventArgs(
        string url,
        string? title,
        SourceSpan sourceRange,
        MarkdownLinkInputKind inputKind,
        MarkdownInputModifiers modifiers,
        MarkdownLinkDisposition requestedDisposition,
        string? action = null)
    {
        Url = url ?? string.Empty;
        Title = title;
        SourceRange = sourceRange;
        InputKind = inputKind;
        Modifiers = modifiers;
        RequestedDisposition = requestedDisposition;
        Action = string.IsNullOrWhiteSpace(action) ? null : action;
        if (Uri.TryCreate(Url, UriKind.RelativeOrAbsolute, out var uri))
            Uri = uri;
    }
    /// <summary>Gets the link URL.</summary>
    public string Url { get; }

    /// <summary>Gets the optional link title.</summary>
    public string? Title { get; }

    /// <summary>Gets the parsed relative or absolute URI, when valid.</summary>
    public Uri? Uri { get; }

    /// <summary>Gets the declarative host action identifier, when present.</summary>
    public string? Action { get; }

    /// <summary>Gets the half-open UTF-16 source range of the activated link.</summary>
    public SourceSpan SourceRange { get; }

    /// <summary>Gets the input modality that activated the link.</summary>
    public MarkdownLinkInputKind InputKind { get; }

    /// <summary>Gets keyboard modifiers active for the activation.</summary>
    public MarkdownInputModifiers Modifiers { get; }

    /// <summary>Gets the navigation disposition requested by the gesture.</summary>
    public MarkdownLinkDisposition RequestedDisposition { get; }

    /// <summary>Gets or sets whether the host handled the activation.</summary>
    public bool Handled { get; set; }
}

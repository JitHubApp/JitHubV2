namespace MarkdownRenderer.Extensions;

/// <summary>
/// Stable declarative attributes understood by the native WinUI adapters.
/// Values use invariant culture and unknown attributes are ignored.
/// </summary>
public static class MarkdownContentAttributes
{
    /// <summary><c>true</c> when a list is ordered.</summary>
    public const string ListOrdered = "list.ordered";

    /// <summary>One-based starting ordinal for an ordered list.</summary>
    public const string ListStart = "list.start";

    /// <summary><c>true</c> when a table row contains column headers.</summary>
    public const string TableHeaderRow = "table.header";

    /// <summary>Optional image width in DIPs or as a percentage such as <c>50%</c>.</summary>
    public const string ImageWidth = "image.width";

    /// <summary>Optional image height in DIPs or as a percentage such as <c>50%</c>.</summary>
    public const string ImageHeight = "image.height";

    /// <summary>Optional title/help text for links and images.</summary>
    public const string Title = "content.title";

    /// <summary>Optional <c>true</c>/<c>false</c> line-number override for code blocks.</summary>
    public const string CodeShowLineNumbers = "code.lineNumbers";

    /// <summary>Optional one-based first line number for code blocks.</summary>
    public const string CodeStartLine = "code.startLine";

    /// <summary>
    /// Marks a cheap placeholder that the host replaces with the renderer's
    /// full output before it becomes visible. Set it only while
    /// <see cref="MarkdownExtensionContext.CanDeferOffscreenScenes"/> is
    /// <see langword="true"/>, on content that spans the whole syntax node,
    /// with a value from <see cref="MarkdownExtensionContext.CreateDeferredSceneMarker"/>.
    /// </summary>
    public const string DeferredScene = "content.deferredScene";
}

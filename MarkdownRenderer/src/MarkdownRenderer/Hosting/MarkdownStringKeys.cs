namespace MarkdownRenderer.Hosting;

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
    /// <summary>Accessible status shown when embedded content is unavailable.</summary>
    public const string EmbeddedContentUnavailable = "EmbeddedContent.Unavailable";
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

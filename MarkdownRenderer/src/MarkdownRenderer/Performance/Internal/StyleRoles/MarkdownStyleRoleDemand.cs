using System;

namespace MarkdownRenderer.Performance.Internal.StyleRoles;

/// <summary>
/// Built-in style roles a document can read. Bit positions intentionally match
/// ThemeResolver.BuiltInElementKeys.
/// </summary>
[Flags]
internal enum MarkdownStyleRoleDemand : ulong
{
    None = 0,
    Heading1 = 1UL << 0,
    Heading2 = 1UL << 1,
    Heading3 = 1UL << 2,
    Heading4 = 1UL << 3,
    Heading5 = 1UL << 4,
    Heading6 = 1UL << 5,
    Body = 1UL << 6,
    CodeInline = 1UL << 7,
    CodeBlock = 1UL << 8,
    CodeBlockHeader = 1UL << 9,
    CodeBlockLanguage = 1UL << 10,
    CodeBlockGutter = 1UL << 11,
    CodeBlockLineNumber = 1UL << 12,
    Quote = 1UL << 13,
    Link = 1UL << 14,
    Strong = 1UL << 15,
    Emphasis = 1UL << 16,
    Strikethrough = 1UL << 17,
    Subscript = 1UL << 18,
    Superscript = 1UL << 19,
    Inserted = 1UL << 20,
    Marked = 1UL << 21,
    Abbreviation = 1UL << 22,
    ListMarker = 1UL << 23,
    ThematicBreak = 1UL << 24,
    ImageCaption = 1UL << 25,
    Figure = 1UL << 26,
    FigureCaption = 1UL << 27,
    Diagram = 1UL << 28,
    Math = 1UL << 29,
    DefinitionTerm = 1UL << 30,
    DefinitionDescription = 1UL << 31,
    Table = 1UL << 32,
    TableHeader = 1UL << 33,
    TableCell = 1UL << 34,
    AlertNote = 1UL << 35,
    AlertTip = 1UL << 36,
    AlertImportant = 1UL << 37,
    AlertWarning = 1UL << 38,
    AlertCaution = 1UL << 39,
}

using System;

namespace MarkdownRenderer.Engine.Internal;

[Flags]
internal enum MarkdownProfileFeatures
{
    None = 0,
    CommonMark = 1 << 0,
    PipeTables = 1 << 1,
    TaskLists = 1 << 2,
    AutoLinks = 1 << 3,
    Strikethrough = 1 << 4,
    Footnotes = 1 << 5,
    Emoji = 1 << 6,
    GenericAttributes = 1 << 7,
    Alerts = 1 << 8,
    DefinitionLists = 1 << 9,
    Abbreviations = 1 << 10,
    Figures = 1 << 11,
    GitHubReadmeMarker = 1 << 12,
    MarkdownExtraMarker = 1 << 13,
    Gfm029Marker = 1 << 14,
    GfmTagFilter = 1 << 15,
}

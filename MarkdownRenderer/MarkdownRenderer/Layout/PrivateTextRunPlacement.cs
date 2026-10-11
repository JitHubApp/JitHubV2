namespace MarkdownRenderer.Layout;

/// <summary>
/// Closed built-in text recipes. Never invoke a renderer, extension, virtual
/// cloning hook or parser while forking an accepted placement tree.
/// </summary>
internal static class PrivateTextRunPlacement
{
    internal static bool IsSupported(InlineRun run) => run is
        SourceSliceTextRun or CodeInlineRun or StrongRun or EmphasisRun or StrikethroughRun or
        SubscriptRun or SuperscriptRun or InsertedRun or MarkedRun or
        AbbreviationRun or LineBreakRun ||
        run is TextRun text && text.GetType() == typeof(TextRun);

    internal static InlineRun? Clone(InlineRun run) => run switch
    {
        SourceSliceTextRun sourceSlice => sourceSlice.CloneForPrivateRasterPlacement(),
        TextRun text when text.GetType() == typeof(TextRun) => text.CloneForPrivateRasterPlacement(),
        CodeInlineRun code => code.CloneForPrivateRasterPlacement(),
        StrongRun strong => strong.CloneForPrivateRasterPlacement(),
        EmphasisRun emphasis => emphasis.CloneForPrivateRasterPlacement(),
        StrikethroughRun strike => strike.CloneForPrivateRasterPlacement(),
        SubscriptRun subscript => subscript.CloneForPrivateRasterPlacement(),
        SuperscriptRun superscript => superscript.CloneForPrivateRasterPlacement(),
        InsertedRun inserted => inserted.CloneForPrivateRasterPlacement(),
        MarkedRun marked => marked.CloneForPrivateRasterPlacement(),
        AbbreviationRun abbreviation => abbreviation.CloneForPrivateRasterPlacement(),
        LineBreakRun lineBreak => lineBreak.CloneForPrivateRasterPlacement(),
        _ => null,
    };
}

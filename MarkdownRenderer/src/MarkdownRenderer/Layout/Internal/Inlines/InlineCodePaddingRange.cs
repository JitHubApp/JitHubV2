namespace MarkdownRenderer.Layout.Internal.Inlines;

/// <summary>
/// One inline-code span awaiting horizontal padding, with its endpoint text
/// elements and caret directions captured before any spacing mutation.
/// </summary>
internal struct InlineCodePaddingRange
{
    public InlineCodePaddingRange(int start, int length, float leftPadding, float rightPadding)
    {
        Start = start;
        Length = length;
        LeftPadding = leftPadding;
        RightPadding = rightPadding;
    }

    public int Start;
    public int Length;
    public float LeftPadding;
    public float RightPadding;
    public int FirstTextElementLength;
    public int LastTextElementStart;
    public int LastTextElementLength;
    public bool FirstLeadingIsLeft;
    public bool LastLeadingIsLeft;
    public bool HasVisualPaddingAnchors;
    public InlineCodePaddingAnchor VisualLeftAnchor;
    public InlineCodePaddingAnchor VisualRightAnchor;
}

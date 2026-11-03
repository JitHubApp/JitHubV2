namespace MarkdownRenderer.Layout;

internal sealed class LineBreakRun : InlineRun
{
    private readonly string _text;

    /// <summary>
    /// Whether this hard break is represented by a same-length blank in layout.
    /// The semantic text remains a newline; this avoids materializing an extra
    /// empty terminal line for standalone breaks or a break before a block.
    /// </summary>
    internal bool UseSingleLineLayout { get; set; }

    public LineBreakRun(bool isHard)
    {
        _text = isHard ? "\n" : " ";
        RenderedLength = 1;
    }

    private LineBreakRun(LineBreakRun source) : base(source)
    {
        _text = source._text;
        UseSingleLineLayout = source.UseSingleLineLayout;
    }

    internal LineBreakRun CloneForPrivateRasterPlacement() => new(this);

    public override string Text => _text;

    internal override string LayoutText =>
        UseSingleLineLayout && _text == "\n" ? " " : _text;
}

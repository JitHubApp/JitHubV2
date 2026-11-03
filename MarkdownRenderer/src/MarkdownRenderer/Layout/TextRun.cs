using System.Threading;

namespace MarkdownRenderer.Layout;

internal class TextRun : InlineRun
{
    // Keep the ordinary TextRun field shape compact. Direct-source subclasses
    // publish their optional string cache into this same slot when a caller
    // explicitly asks for Text.
    private string _text;
    public TextRun(string text) { _text = text ?? string.Empty; RenderedLength = _text.Length; }
    protected TextRun(TextRun source) : base(source) => _text = Volatile.Read(ref source._text);
    internal TextRun CloneForPrivateRasterPlacement() => new(this);

    protected string? ReadMaterializedText()
    {
        string value = Volatile.Read(ref _text);
        return value.Length == 0 ? null : value;
    }

    protected string PublishMaterializedText(string value)
    {
        string existing = Interlocked.CompareExchange(ref _text, value, string.Empty);
        return existing.Length == 0 ? value : existing;
    }

    public override string Text => _text;
}

using System;
using System.Text;

namespace MarkdownRenderer.Layout.Internal.Inlines.Runs;

/// <summary>
/// A literal run whose rendered UTF-16 content is exactly one range of the
/// immutable Markdown source. Its string-facing API stays compatible, while
/// layout and semantic construction can consume the source range directly.
/// </summary>
internal sealed class SourceSliceTextRun : TextRun
{
    private readonly string _sourceText;
    private readonly int _sourceStart;

    internal SourceSliceTextRun(string sourceText, int sourceStart, int length)
        : base(string.Empty)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        if (length <= 0 || sourceStart < 0 || sourceStart > sourceText.Length - length)
            throw new ArgumentOutOfRangeException(nameof(length));

        _sourceText = sourceText;
        _sourceStart = sourceStart;
        RenderedLength = length;
    }

    private SourceSliceTextRun(SourceSliceTextRun source) : base(source)
    {
        _sourceText = source._sourceText;
        _sourceStart = source._sourceStart;
    }

    internal string SourceText => _sourceText;
    internal int SourceStart => _sourceStart;
    internal int SliceLength => RenderedLength;

    /// <summary>Whether a caller has read <see cref="Text"/> and published its string cache.</summary>
    internal bool HasMaterializedText => ReadMaterializedText() is not null;

    internal new SourceSliceTextRun CloneForPrivateRasterPlacement() => new(this);

    public override string Text
    {
        get
        {
            string? cached = ReadMaterializedText();
            if (cached is not null)
                return cached;

            string materialized = _sourceText.Substring(_sourceStart, RenderedLength);
            return PublishMaterializedText(materialized);
        }
    }

    internal new void AppendLayoutTextTo(StringBuilder builder)
        => builder.Append(_sourceText, _sourceStart, RenderedLength);
}

using System.Text;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// Decodes an already-owned UTF-8 snapshot directly into XmlReader's character
/// buffer. It does not reserve another input byte buffer or a whole-source string.
/// Only use after excluding XML encoding declarations and UTF-16/32 signatures.
/// </summary>
internal sealed class SvgUtf8TextReader : TextReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private SvgSourceBuffer.ReadLease? _source;
    private readonly Decoder _decoder = StrictUtf8.GetDecoder();
    private readonly CancellationToken _cancellationToken;
    private int _offset;
    private char? _pendingCharacter;

    public SvgUtf8TextReader(
        SvgSourceBuffer.ReadLease source,
        int offset,
        CancellationToken cancellationToken = default)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        if ((uint)offset > (uint)source.Length)
        {
            source.Dispose();
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        _offset = offset;
        _cancellationToken = cancellationToken;
    }

    public override int Read(char[] buffer, int index, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (index > buffer.Length - count)
            throw new ArgumentException("The requested character range exceeds the destination.");
        return Read(buffer.AsSpan(index, count));
    }

    public override int Read(Span<char> buffer)
    {
        SvgSourceBuffer.ReadLease source = _source ?? throw new ObjectDisposedException(nameof(SvgUtf8TextReader));
        _cancellationToken.ThrowIfCancellationRequested();
        if (buffer.IsEmpty)
            return 0;
        if (_pendingCharacter is char pending)
        {
            buffer[0] = pending;
            _pendingCharacter = null;
            return 1;
        }

        // Decoder.Convert cannot split a UTF-16 surrogate pair into a single
        // destination character. Preserve TextReader's one-character contract
        // without changing the direct-to-XmlReader path for normal buffers.
        Span<char> singleRead = stackalloc char[2];
        Span<char> destination = buffer.Length == 1 ? singleRead : buffer;
        int totalChars = 0;
        while (true)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            ReadOnlySpan<byte> sourceBytes = source.GetSpanAt(_offset);
            bool flush = _offset + sourceBytes.Length == source.Length;
            _decoder.Convert(sourceBytes, destination[totalChars..], flush,
                out int bytesRead, out int charsRead, out _);
            _offset += bytesRead;
            totalChars += charsRead;

            if (buffer.Length == 1 && totalChars > 0)
            {
                buffer[0] = singleRead[0];
                if (totalChars == 2)
                    _pendingCharacter = singleRead[1];
                return 1;
            }

            if (totalChars > 0 || _offset >= source.Length)
                return totalChars;

            if (bytesRead == 0 && charsRead == 0)
            {
                throw new InvalidOperationException("The UTF-8 decoder made no progress before end of source.");
            }
        }
    }

    public override int Read()
    {
        Span<char> character = stackalloc char[1];
        return Read(character) == 0 ? -1 : character[0];
    }

    protected override void Dispose(bool disposing)
    {
        Interlocked.Exchange(ref _source, null)?.Dispose();
        _pendingCharacter = null;
        base.Dispose(disposing);
    }
}

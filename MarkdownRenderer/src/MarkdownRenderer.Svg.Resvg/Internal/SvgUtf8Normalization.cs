using System.Runtime.CompilerServices;
using System.Text;
using System.Xml;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// Detects non-UTF-8 SVG input and re-encodes it as bounded UTF-8 segments
/// before the streaming inspection reads it.
/// </summary>
internal static class SvgUtf8Normalization
{
    internal static bool RequiresUtf8Normalization(ReadOnlySpan<byte> source, string? declaredEncoding)
    {
        if (HasNonUtf8EncodingPrefix(source))
            return true;
        if (string.IsNullOrWhiteSpace(declaredEncoding))
            return false;

        try
        {
            return Encoding.GetEncoding(declaredEncoding).CodePage != Encoding.UTF8.CodePage;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            // The XmlReader has already accepted and decoded the declaration;
            // if this process cannot name that encoding directly, normalize it
            // rather than handing the original bytes to the UTF-8 worker.
            return true;
        }
    }

    internal static bool HasNonUtf8EncodingPrefix(ReadOnlySpan<byte> source)
    {
        if (source.StartsWith<byte>([0xef, 0xbb, 0xbf]))
            return false;
        if (source.StartsWith<byte>([0xff, 0xfe, 0x00, 0x00]) ||
            source.StartsWith<byte>([0x00, 0x00, 0xfe, 0xff]) ||
            source.StartsWith<byte>([0xff, 0xfe]) ||
            source.StartsWith<byte>([0xfe, 0xff]))
        {
            return true;
        }

        if (source.Length < 4)
            return false;

        byte first = source[0];
        byte second = source[1];
        byte third = source[2];
        byte fourth = source[3];
        // XML's initial markup bytes let XmlReader identify BOM-less UTF-16/32
        // documents; recognize those same NUL patterns, including leading XML
        // whitespace, before treating the source as the UTF-8 fast path.
        return (first == 0 && second == 0 && third == 0 && fourth != 0) ||
               (first != 0 && second == 0 && third == 0 && fourth == 0) ||
               (first == 0 && third == 0 && second != 0 && fourth != 0) ||
               (first != 0 && second == 0 && third != 0 && fourth == 0);
    }

    // Return only segmented output, never an input stream or source alias. The
    // caller owns the returned segments and must transfer or dispose them; a
    // cleanup failure still disposes untransferred output.
    internal static async Task<BoundedOutputStream> NormalizeToUtf8SegmentsAsync(
        byte[] source,
        ResvgMarkdownSvgRendererOptions options,
        CancellationToken cancellationToken,
        Action? beforeFirstSegmentAllocation = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        using SvgSourceBuffer snapshot = SvgSourceBuffer.CopyFrom(source, cancellationToken);
        return await NormalizeToUtf8SegmentsAsync(
            snapshot,
            options,
            cancellationToken,
            beforeFirstSegmentAllocation).ConfigureAwait(false);
    }

    internal static async Task<BoundedOutputStream> NormalizeToUtf8SegmentsAsync(
        SvgSourceBuffer source,
        ResvgMarkdownSvgRendererOptions options,
        CancellationToken cancellationToken,
        Action? beforeFirstSegmentAllocation = null)
    {
        SvgSourceReadLease? inputLease = null;
        SvgSourceStream? input = null;
        XmlReader? reader = null;
        BoundedOutputStream? output = null;
        XmlWriter? writer = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                inputLease = source.AcquireReadLease();
                input = new SvgSourceStream(inputLease, cancellationToken);
                inputLease = null;
                reader = XmlReader.Create(
                    input,
                    SvgPreflight.CreateReaderSettings(options.MaxSourceBytes, ignoreComments: false));
                output = new BoundedOutputStream(
                    options.MaxSourceBytes,
                    cancellationToken,
                    beforeFirstSegmentAllocation);
                beforeFirstSegmentAllocation = null;
                writer = XmlWriter.Create(output, CreateWriterSettings());
                await CopyNodesAsync(reader, writer, options, cancellationToken).ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();
                await writer.FlushAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            finally
            {
                DisposeReadChain(ref writer, ref reader, ref input, ref inputLease);
            }
            cancellationToken.ThrowIfCancellationRequested();
            BoundedOutputStream result = output;
            output = null;
            return result;
        }
        finally
        {
            output?.Dispose();
            output = null;
            beforeFirstSegmentAllocation = null;
        }
    }

    private static XmlWriterSettings CreateWriterSettings() => new()
    {
        Async = true,
        CloseOutput = false,
        Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        Indent = false,
        NewLineHandling = NewLineHandling.None,
        OmitXmlDeclaration = false,
    };

    /// <summary>
    /// Re-encodes every node from <paramref name="reader"/> through
    /// <paramref name="writer"/>, replacing the XML declaration and rejecting
    /// over-deep, DTD and external-stylesheet nodes as they are reached.
    /// </summary>
    /// <param name="reader">The reader over the original source.</param>
    /// <param name="writer">The UTF-8 writer over the bounded output.</param>
    /// <param name="options">The renderer options.</param>
    /// <param name="cancellationToken">Cancels normalization.</param>
    /// <returns>A task that completes when the reader is exhausted.</returns>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private static async ValueTask CopyNodesAsync(
        XmlReader reader,
        XmlWriter writer,
        ResvgMarkdownSvgRendererOptions options,
        CancellationToken cancellationToken)
    {
        bool readNext = true;
        while (true)
        {
            if (reader.EOF || reader.ReadState == ReadState.EndOfFile)
                break;
            if (readNext && !await reader.ReadAsync().ConfigureAwait(false))
                break;
            readNext = true;
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.NodeType == XmlNodeType.XmlDeclaration)
            {
                await WriteDeclarationAsync(reader, writer).ConfigureAwait(false);
                continue;
            }

            ThrowIfForbiddenNode(reader, options);
            await writer.WriteNodeAsync(reader, defattr: true).ConfigureAwait(false);
            // WriteNode consumes the current node and leaves the reader on
            // its next sibling; that node must be processed before another
            // ReadAsync advances the stream.
            if (reader.EOF || reader.ReadState == ReadState.EndOfFile)
                break;
            readNext = false;
        }
    }

    /// <summary>
    /// WriteNode preserves the original declaration verbatim, including its
    /// non-UTF-8 encoding. Let the configured writer emit the matching
    /// declaration while preserving the authored standalone contract (XML 1.0
    /// was validated).
    /// </summary>
    /// <param name="reader">The reader positioned on the XML declaration.</param>
    /// <param name="writer">The UTF-8 writer.</param>
    /// <returns>A task that completes when the declaration is written.</returns>
    private static Task WriteDeclarationAsync(XmlReader reader, XmlWriter writer) =>
        reader.GetAttribute("standalone") switch
        {
            "yes" => writer.WriteStartDocumentAsync(standalone: true),
            "no" => writer.WriteStartDocumentAsync(standalone: false),
            _ => writer.WriteStartDocumentAsync(),
        };

    private static void ThrowIfForbiddenNode(XmlReader reader, ResvgMarkdownSvgRendererOptions options)
    {
        if (reader.Depth > options.MaxXmlDepth)
            throw SvgPreflightFailures.Resource("The SVG exceeds the XML-depth ceiling.");
        if (reader.NodeType == XmlNodeType.DocumentType)
        {
            throw SvgPreflightFailures.Unsupported("SVG DTDs and entities are forbidden.");
        }
        if (reader.NodeType == XmlNodeType.ProcessingInstruction &&
            reader.Name.Equals("xml-stylesheet", StringComparison.OrdinalIgnoreCase))
        {
            throw SvgPreflightFailures.Unsupported("External SVG stylesheets are forbidden.");
        }
    }

    /// <summary>
    /// Disposes the writer, reader, input stream and input lease in that
    /// order, continuing past a failure, and clears each reference so the
    /// async frame no longer roots it.
    /// </summary>
    /// <param name="writer">The UTF-8 writer, if created.</param>
    /// <param name="reader">The source reader, if created.</param>
    /// <param name="input">The source stream, if created.</param>
    /// <param name="inputLease">The read lease, if not yet adopted by the stream.</param>
    private static void DisposeReadChain(
        ref XmlWriter? writer,
        ref XmlReader? reader,
        ref SvgSourceStream? input,
        ref SvgSourceReadLease? inputLease)
    {
        try
        {
            writer?.Dispose();
        }
        finally
        {
            writer = null;
            try
            {
                reader?.Dispose();
            }
            finally
            {
                reader = null;
                try
                {
                    input?.Dispose();
                }
                finally
                {
                    input = null;
                    try
                    {
                        inputLease?.Dispose();
                    }
                    finally
                    {
                        inputLease = null;
                    }
                }
            }
        }
    }
}

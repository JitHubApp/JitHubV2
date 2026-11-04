using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage.Streams;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Reads the bytes of a source the built-in loader may open without a host
/// resolver (a data URI, a local file, or a packaged URI) within a compressed
/// input budget, admitting the source bytes with the performance session first.
/// The caller owns the returned reservation.
/// </summary>
internal sealed class BuiltInImageSourceReader
{
    private readonly MarkdownLayoutContext _context;
    private readonly object _preparationOwner;

    /// <param name="context">The layout context whose performance session admits source bytes.</param>
    /// <param name="preparationOwner">
    /// The owner the session charges when the context names no effective
    /// image preparation owner.
    /// </param>
    internal BuiltInImageSourceReader(MarkdownLayoutContext context, object preparationOwner)
    {
        _context = context;
        _preparationOwner = preparationOwner;
    }

    internal async Task<(byte[] Bytes, IDisposable? Reservation)> ReadAsync(
        MarkdownBuiltInImageSource source,
        int maxBytes,
        string assetKind,
        CancellationToken cancellationToken)
    {
        if (!source.CanLoadWithoutResolver)
            throw new InvalidOperationException("The source requires a host image resolver.");

        cancellationToken.ThrowIfCancellationRequested();
        if (source.Kind == MarkdownBuiltInImageSourceKind.Data)
        {
            // Measure without a byte buffer, then admit before decoding. This
            // whole method runs off the UI thread, including local opens.
            return await ReadDataUriAsync(
                source.Uri.OriginalString, maxBytes, cancellationToken).ConfigureAwait(false);
        }

        if (source.Kind == MarkdownBuiltInImageSourceKind.Local && source.Uri.IsFile)
            return await ReadLocalFileAsync(source, maxBytes, assetKind, cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        using IRandomAccessStreamWithContentType randomAccessStream = await RandomAccessStreamReference
            .CreateFromUri(source.Uri)
            .OpenReadAsync()
            .AsTask()
            .ConfigureAwait(false);
        // Do not cancel the WinRT bridge task and dispose its returned stream
        // while the underlying open is still completing. The enclosing image
        // work lease keeps snapshot retirement pending until this native
        // operation has actually returned.
        cancellationToken.ThrowIfCancellationRequested();
        if (randomAccessStream.Size == 0 || randomAccessStream.Size > (ulong)maxBytes)
        {
            throw new InvalidDataException(
                $"The local {assetKind} exceeds the compressed input budget.");
        }

        int byteLength = checked((int)randomAccessStream.Size);
        // DataReader keeps a native input buffer alongside the managed array.
        // Cover both before either is materialized; do not cancel and dispose
        // the native operation until it has really returned.
        IDisposable? nativeReservation = await ReserveSourceBytesAsync(
            checked(byteLength * 2L), cancellationToken).ConfigureAwait(false);
        try
        {
            uint requestedLength = (uint)byteLength;
            byte[] result = new byte[byteLength];
            using DataReader reader = new(randomAccessStream.GetInputStreamAt(0));
            cancellationToken.ThrowIfCancellationRequested();
            uint loaded = await reader.LoadAsync(requestedLength)
                .AsTask()
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (loaded != requestedLength)
                throw new EndOfStreamException($"The local {assetKind} ended before its declared size.");

            reader.ReadBytes(result);
            return (result, nativeReservation);
        }
        catch
        {
            nativeReservation?.Dispose();
            throw;
        }
    }

    internal async Task<(byte[] Bytes, IDisposable? Reservation)> ReadDataUriAsync(
        string source, int maxBytes, CancellationToken cancellationToken)
    {
        int length = MarkdownDataUriDecoder.GetDecodedLength(source, maxBytes, cancellationToken);
        IDisposable? reservation = await ReserveSourceBytesAsync(length, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return (MarkdownDataUriDecoder.Decode(source, maxBytes, cancellationToken), reservation);
        }
        catch
        {
            reservation?.Dispose();
            throw;
        }
    }

    private async Task<(byte[] Bytes, IDisposable? Reservation)> ReadLocalFileAsync(
        MarkdownBuiltInImageSource source,
        int maxBytes,
        string assetKind,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            source.Uri.LocalPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length == 0 || stream.Length > maxBytes)
        {
            throw new InvalidDataException(
                $"The local {assetKind} exceeds the compressed input budget.");
        }

        int fileLength = checked((int)stream.Length);
        IDisposable? reservation = await ReserveSourceBytesAsync(fileLength, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            byte[] bytes = new byte[fileLength];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            return (bytes, reservation);
        }
        catch
        {
            reservation?.Dispose();
            throw;
        }
    }

    private async ValueTask<IDisposable?> ReserveSourceBytesAsync(
        long bytes, CancellationToken cancellationToken) =>
        _context.PerformanceSession is { } session
            ? await session.ReserveImageSourceBytesAsync(
                _context.EffectiveImagePreparationOwner ?? _preparationOwner, bytes, cancellationToken).ConfigureAwait(false)
            : null;
}

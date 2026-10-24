using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Images;

namespace JitHub.Services;

/// <summary>
/// Reads image responses without materializing an unbounded HttpContent buffer and,
/// when admission is supplied, obtains byte capacity before allocating payload arrays.
/// </summary>
internal static class AdmittedImageContentReader
{
    internal static async Task<(byte[] Bytes, IDisposable? Lease)> ReadAsync(
        HttpContent content,
        int maximumBytes,
        IMarkdownImageSourceByteAdmission? admission,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (maximumBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));

        if (content.Headers.ContentLength is long contentLength &&
            (contentLength < 0 || contentLength > maximumBytes))
        {
            throw new InvalidDataException($"Remote image exceeds the {maximumBytes}-byte limit.");
        }

        if (content.Headers.ContentLength is long knownLength)
        {
            if (admission is not null && knownLength > admission.MaximumReservationBytes)
                ThrowAdmissionLimitExceeded(admission, maximumBytes);

            IDisposable? lease = null;
            byte[] bytes = Array.Empty<byte>();
            byte[] extra = Array.Empty<byte>();
            try
            {
                if (admission is not null && knownLength > 0)
                    lease = await admission.ReserveAsync(knownLength, cancellationToken).ConfigureAwait(false);

                // Some HttpContent implementations buffer while opening their stream;
                // obtain admission first, then allocate the one exact-size destination.
                await using Stream knownInput = await content.ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                bytes = new byte[checked((int)knownLength)];
                await knownInput.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
                extra = new byte[1];
                if (await knownInput.ReadAsync(extra, cancellationToken).ConfigureAwait(false) != 0)
                    throw new InvalidDataException("Remote image exceeds its declared content length.");

                IDisposable? transferred = lease;
                lease = null;
                byte[] result = bytes;
                bytes = Array.Empty<byte>();
                return (result, transferred);
            }
            finally
            {
                // A faulted or completed async state machine can outlive this
                // method call; clear payload locals before their reservation ends.
                bytes = Array.Empty<byte>();
                extra = Array.Empty<byte>();
                lease?.Dispose();
            }
        }

        if (admission is not null)
            return await ReadUnknownLengthAdmittedAsync(content, maximumBytes, admission, cancellationToken)
                .ConfigureAwait(false);

        await using Stream input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using MemoryStream output = new();
        byte[] buffer = new byte[81920];
        while (true)
        {
            int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return (output.ToArray(), null);

            if (read > maximumBytes - output.Length)
                throw new InvalidDataException($"Remote image exceeds the {maximumBytes}-byte limit.");

            output.Write(buffer, 0, read);
        }
    }

    private static async Task<(byte[] Bytes, IDisposable Lease)> ReadUnknownLengthAdmittedAsync(
        HttpContent content,
        int maximumBytes,
        IMarkdownImageSourceByteAdmission admission,
        CancellationToken cancellationToken)
    {
        // Admit both the initial scratch buffer and any exact-size result. Larger
        // streams spill to a delete-on-close file, releasing scratch capacity before
        // waiting for the final exact-size reservation.
        int initialCapacity = checked((int)Math.Min(
            Math.Min(1024L * 1024, admission.MaximumReservationBytes / 2), maximumBytes));
        if (initialCapacity < 1)
            ThrowAdmissionLimitExceeded(admission, maximumBytes);

        IDisposable? initialLease = await admission.ReserveAsync(
            initialCapacity * 2L, cancellationToken).ConfigureAwait(false);
        byte[] buffer = Array.Empty<byte>();
        byte[] one = Array.Empty<byte>();
        try
        {
            await using Stream input = await content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            buffer = new byte[initialCapacity];
            int length = 0;
            while (length < buffer.Length)
            {
                int read = await input.ReadAsync(buffer.AsMemory(length), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    byte[] exact = buffer.AsSpan(0, length).ToArray();
                    buffer = Array.Empty<byte>();
                    IDisposable transferred = initialLease;
                    initialLease = null;
                    return (exact, transferred);
                }
                length += read;
            }

            one = new byte[1];
            if (await input.ReadAsync(one, cancellationToken).ConfigureAwait(false) == 0)
            {
                byte[] exact = buffer;
                buffer = Array.Empty<byte>();
                one = Array.Empty<byte>();
                IDisposable transferred = initialLease;
                initialLease = null;
                return (exact, transferred);
            }

            int total = checked(length + 1);
            ThrowIfBeyondLimits(total, maximumBytes, admission);

            string temporaryPath = Path.Combine(Path.GetTempPath(), $"jithub-image-{Guid.NewGuid():N}.tmp");
            // BufferSize=1 disables FileStream's private write buffer. Otherwise
            // small writes could retain admitted payload bytes while final admission waits.
            await using FileStream spool = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                1,
                FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            await spool.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            await spool.WriteAsync(one, cancellationToken).ConfigureAwait(false);

            while (true)
            {
                long effectiveMaximum = Math.Min(maximumBytes, admission.MaximumReservationBytes);
                long remaining = effectiveMaximum - total;
                int batchLimit = checked((int)Math.Min(buffer.Length, remaining + 1));
                int buffered = 0;
                bool reachedEof = false;
                while (buffered < batchLimit)
                {
                    int read = await input.ReadAsync(
                            buffer.AsMemory(buffered, batchLimit - buffered),
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0)
                    {
                        reachedEof = true;
                        break;
                    }

                    buffered += read;
                    if (buffered > remaining)
                        ThrowIfBeyondLimits((long)total + buffered, maximumBytes, admission);
                }

                if (buffered == 0)
                    break;
                await spool.WriteAsync(buffer.AsMemory(0, buffered), cancellationToken).ConfigureAwait(false);
                total += buffered;
                if (reachedEof)
                    break;
            }

            // Do not keep scratch payloads alive while waiting for exact admission.
            buffer = Array.Empty<byte>();
            one = Array.Empty<byte>();
            initialLease.Dispose();
            initialLease = null;
            IDisposable? finalLease = await admission.ReserveAsync(total, cancellationToken)
                .ConfigureAwait(false);
            byte[] bytes = Array.Empty<byte>();
            try
            {
                bytes = new byte[total];
                spool.Position = 0;
                await spool.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
                byte[] result = bytes;
                bytes = Array.Empty<byte>();
                IDisposable transferred = finalLease;
                finalLease = null;
                return (result, transferred);
            }
            finally
            {
                bytes = Array.Empty<byte>();
                finalLease?.Dispose();
            }
        }
        finally
        {
            // The reader task may remain referenced after completion. Clear the
            // scratch fields before releasing their reservation so a completed
            // or faulted async state machine cannot keep source bytes unaccounted.
            buffer = Array.Empty<byte>();
            one = Array.Empty<byte>();
            initialLease?.Dispose();
        }
    }

    private static void ThrowIfBeyondLimits(
        long totalBytes,
        int maximumBytes,
        IMarkdownImageSourceByteAdmission admission)
    {
        if (totalBytes > maximumBytes)
            throw new InvalidDataException($"Remote image exceeds the {maximumBytes}-byte limit.");
        if (totalBytes > admission.MaximumReservationBytes)
            ThrowAdmissionLimitExceeded(admission, maximumBytes);
    }

    private static void ThrowAdmissionLimitExceeded(
        IMarkdownImageSourceByteAdmission admission,
        int maximumBytes)
    {
        if (admission.IsSpeculative)
            throw new MarkdownImageSourceDeferredException();
        throw new InvalidDataException(
            $"Remote image exceeds the {Math.Min(maximumBytes, admission.MaximumReservationBytes)}-byte admission limit.");
    }
}

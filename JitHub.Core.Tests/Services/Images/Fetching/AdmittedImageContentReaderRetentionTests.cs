using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Core.Tests.TestSupport;
using MarkdownRenderer.Images;
using MarkdownRenderer.Performance;
using Xunit;
using static JitHub.Core.Tests.Services.Images.Fetching.GitHubRepositoryImageClientTestDoubles;

namespace JitHub.Core.Tests.Services.Images.Fetching;

/// <summary>
/// Which payload and scratch buffers the admitted image content reader keeps reachable
/// after its read task completes, faults or is canceled.
/// </summary>
public sealed class AdmittedImageContentReaderRetentionTests
{
    [Fact]
    public async Task FaultedKnownLengthReaderTaskDoesNotRetainItsDestinationBuffer()
    {
        ReaderRun run = StartTruncatedKnownLengthRead();
        using (run.Content)
        {
            await Assert.ThrowsAsync<EndOfStreamException>(() => run.Task);
            Assert.NotEmpty(run.DestinationBuffers);
            AssertCollected(run.DestinationBuffers);
            Assert.Equal(0, run.Admission.ReservedBytes);
            GC.KeepAlive(run.Task);
        }
    }

    [Fact]
    public async Task FaultedChunkedReaderTaskDoesNotRetainScratchOrProbeBuffers()
    {
        ReaderRun run = StartChunkedAdmissionFailure(speculative: false);
        using (run.Content)
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => run.Task);
            Assert.Contains(run.DestinationBuffers, destination => destination.RequestedLength == 1);
            AssertCollected(run.DestinationBuffers);
            Assert.Equal(0, run.Admission.ReservedBytes);
            GC.KeepAlive(run.Task);
        }
    }

    [Fact]
    public async Task SuccessfulShortCopyReleasesScratchWhileTaskResultAndLeaseRemainRetained()
    {
        ReaderRun run = StartChunkedShortRead();
        using (run.Content)
        {
            (byte[] bytes, IDisposable? lease) = await run.Task;
            try
            {
                Assert.Equal(new byte[] { 5, 4, 3, 2, 1 }, bytes);
                Assert.Equal(16, run.Admission.ReservedBytes);
                AssertCollected(run.DestinationBuffers);
                GC.KeepAlive(bytes);
                GC.KeepAlive(lease);
                GC.KeepAlive(run.Task);
            }
            finally
            {
                lease?.Dispose();
            }
        }

        Assert.Equal(0, run.Admission.ReservedBytes);
    }

    [Fact]
    public async Task SuccessfulSpoolCopyDoesNotRetainScratchWhileTaskResultAndLeaseRemainRetained()
    {
        ReaderRun run = StartChunkedSpoolRead();
        using (run.Content)
        {
            (byte[] bytes, IDisposable? lease) = await run.Task;
            try
            {
                Assert.Equal(new byte[40], bytes);
                Assert.Equal(2, run.Admission.ReserveCalls);
                Assert.Equal(0, run.Admission.ReservedBeforeSecondRequest);
                Assert.Equal(40, run.Admission.ReservedBytes);
                AssertCollected(run.DestinationBuffers);
                GC.KeepAlive(bytes);
                GC.KeepAlive(lease);
                GC.KeepAlive(run.Task);
            }
            finally
            {
                lease?.Dispose();
            }
        }

        Assert.Equal(0, run.Admission.ReservedBytes);
    }

    [Fact]
    public async Task CancellationWhileFinalSpoolAdmissionWaitsDoesNotRetainScratchBuffers()
    {
        using CancellationTokenSource cancellation = new();
        ReaderRun run = StartChunkedFinalAdmissionWait(cancellation.Token);
        using (run.Content)
        {
            await run.Admission.SecondReservationStarted.WaitAsync(AsyncTestWait.HangGuard);
            Assert.Equal(2, run.Admission.ReserveCalls);
            Assert.Equal(40, run.Admission.LastRequestedBytes);
            Assert.Equal(0, run.Admission.ReservedBeforeSecondRequest);
            Assert.Equal(0, run.Admission.ReservedBytes);

            // The reader task remains live while it waits for final admission;
            // its scratch destinations must already have been retired.
            AssertCollected(run.DestinationBuffers);

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.Task);
            Assert.Equal(0, run.Admission.ReservedBytes);
            Assert.Equal(0, run.Admission.InvalidReleaseCount);
            GC.KeepAlive(run.Task);
        }
    }
}

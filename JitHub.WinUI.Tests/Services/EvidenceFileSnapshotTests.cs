using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class EvidenceFileSnapshotTests
{
    [Fact]
    public async Task ExclusiveWriterDoesNotTurnTransientSharingIntoAnAuditFailure()
    {
        string directory = Path.Combine(Path.GetTempPath(), "jithub-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string source = Path.Combine(directory, "first-viewport-images-ready.json");
        string destination = Path.Combine(directory, "captured", "first-viewport-images-ready.json");
        byte[] expected = Encoding.UTF8.GetBytes("{\"Generation\":7}");
        try
        {
            await using var writer = new FileStream(source, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await writer.WriteAsync(expected);
            await writer.FlushAsync();

            Task<bool> capture = Task.Run(() => EvidenceFileSnapshot.PreserveIfPresent(
                source, destination, TimeSpan.FromSeconds(2)));
            await Task.Delay(100);
            Assert.False(capture.IsCompleted);
            await writer.DisposeAsync();

            Assert.True(await capture.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal(expected, await File.ReadAllBytesAsync(destination));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(destination)!, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MissingOptionalEvidenceIsNotSynthesized()
    {
        string directory = Path.Combine(Path.GetTempPath(), "jithub-evidence-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(directory, "missing.ndjson");
        string destination = Path.Combine(directory, "captured.ndjson");

        Assert.False(EvidenceFileSnapshot.PreserveIfPresent(
            source, destination, TimeSpan.FromSeconds(1)));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public void PersistentSharingFailureDoesNotReplaceExistingEvidence()
    {
        string directory = Path.Combine(Path.GetTempPath(), "jithub-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string source = Path.Combine(directory, "signal.json");
        string destination = Path.Combine(directory, "captured.json");
        try
        {
            File.WriteAllText(source, "new");
            File.WriteAllText(destination, "previous");
            using var writer = new FileStream(source, FileMode.Open, FileAccess.Write, FileShare.None);

            Assert.Throws<IOException>(() => EvidenceFileSnapshot.PreserveIfPresent(
                source, destination, TimeSpan.FromMilliseconds(80)));
            Assert.Equal("previous", File.ReadAllText(destination));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NonIoPublicationFailureCleansTemporaryFileAndPreservesOriginalFailure(bool accessFailure)
    {
        string directory = Path.Combine(Path.GetTempPath(), "jithub-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string source = Path.Combine(directory, "source.json");
        string destination = Path.Combine(directory, "captured.json");
        Exception failure = accessFailure
            ? new UnauthorizedAccessException("injected publication failure")
            : new InvalidOperationException("injected publication failure");
        try
        {
            File.WriteAllText(source, "new");
            File.WriteAllText(destination, "previous");
            Exception observed = Assert.ThrowsAny<Exception>(() => EvidenceFileSnapshot.PreserveIfPresent(
                source, destination, TimeSpan.FromSeconds(1), (temporary, target) =>
                {
                    Assert.Equal(destination, target);
                    Assert.Equal("new", File.ReadAllText(temporary));
                    throw failure;
                }));
            Assert.Same(failure, observed);
            Assert.Equal("new", File.ReadAllText(source));
            Assert.Equal("previous", File.ReadAllText(destination));
            Assert.Empty(Directory.GetFiles(directory, "*.snapshot-*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void IoPublicationRetryCleansEveryTemporaryFileWithoutReplacingExistingEvidence()
    {
        string directory = Path.Combine(Path.GetTempPath(), "jithub-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string source = Path.Combine(directory, "source.json");
        string destination = Path.Combine(directory, "captured.json");
        var failure = new IOException("injected publication failure");
        int attempts = 0;
        try
        {
            File.WriteAllText(source, "new");
            File.WriteAllText(destination, "previous");
            IOException observed = Assert.Throws<IOException>(() => EvidenceFileSnapshot.PreserveIfPresent(
                source, destination, TimeSpan.FromMilliseconds(100), (temporary, _) =>
                {
                    // A preceding failed attempt must already have cleaned up.
                    Assert.Single(Directory.GetFiles(directory, "*.snapshot-*.tmp"));
                    Assert.Equal("new", File.ReadAllText(temporary));
                    attempts++;
                    throw failure;
                }));
            Assert.True(attempts > 0);
            Assert.Same(failure, observed.InnerException);
            Assert.Equal("previous", File.ReadAllText(destination));
            Assert.Empty(Directory.GetFiles(directory, "*.snapshot-*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

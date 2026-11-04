using System;
using System.IO;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class MarkdownPerformanceSessionDisposalContractTests
{
    [Fact]
    public void DisposedBorrowedSession_RebuildIsQueuedAndRevalidatesControlLifetime()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "MarkdownRenderer",
            "src",
            "MarkdownRenderer",
            "Controls",
            "Providers",
            "PerformanceSessionSubscription.cs"));
        int handlerStart = source.IndexOf("private void OnDisposed(", StringComparison.Ordinal);
        int handlerEnd = source.LastIndexOf('}');

        Assert.True(handlerStart >= 0 && handlerEnd > handlerStart);
        string handler = source[handlerStart..handlerEnd];
        int enqueue = handler.IndexOf("DispatcherQueue?.TryEnqueue(() =>", StringComparison.Ordinal);

        Assert.True(enqueue >= 0);
        string queuedCallback = handler[enqueue..];
        Assert.Contains("ReferenceEquals(sender, Volatile.Read(ref _subscribed))", queuedCallback, StringComparison.Ordinal);
        Assert.Contains("!_lifetime.IsDisposed && !_lifetime.IsUnloaded", queuedCallback, StringComparison.Ordinal);
        Assert.Contains("_rebuild.Request(RebuildReason.Full);", queuedCallback, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the JitHub repository root.");
    }
}

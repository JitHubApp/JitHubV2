using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class DiagnosticsShutdownCoordinatorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "JitHubDiagnosticsShutdownTests",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public async Task DrainAsync_PersistsEveryAcceptedEventBeforeSuccess()
    {
        string path = Path.Combine(_root, "diagnostics.ndjson");
        await using LocalDiagnosticsStore store = new(path, 1024 * 1024, TimeSpan.FromDays(14));
        for (int index = 0; index < 80; index++)
        {
            Assert.True(store.TryAppend(CreateEvent(index)));
        }

        DiagnosticsShutdownResult result = await DiagnosticsShutdownCoordinator.DrainAsync(
            store,
            TimeSpan.FromSeconds(5));

        Assert.Equal(DiagnosticsShutdownStatus.Drained, result.Status);
        Assert.Equal(80, (await File.ReadAllLinesAsync(path)).Length);
    }

    [Fact]
    public async Task DrainAsync_ReportsBoundedTimeoutAndLeavesDrainAliveForProcessExitFallback()
    {
        ControlledStore store = new();
        List<DiagnosticsShutdownResult> reports = [];

        DiagnosticsShutdownResult result = await DiagnosticsShutdownCoordinator.DrainAsync(
            store,
            TimeSpan.FromMilliseconds(25),
            reports.Add);

        Assert.Equal(DiagnosticsShutdownStatus.TimedOut, result.Status);
        Assert.False(result.IsSuccess);
        Assert.Same(result, Assert.Single(reports));
        Assert.False(store.Drain.Task.IsCompleted);

        store.Drain.TrySetResult();
        DiagnosticsShutdownResult fallback = await DiagnosticsShutdownCoordinator.DrainAsync(
            store,
            TimeSpan.FromSeconds(1));
        Assert.Equal(DiagnosticsShutdownStatus.Drained, fallback.Status);
    }

    [Fact]
    public async Task DrainAsync_ReportsStoreFailure()
    {
        ControlledStore store = new(new IOException("disk unavailable"));
        DiagnosticsShutdownResult? reported = null;

        DiagnosticsShutdownResult result = await DiagnosticsShutdownCoordinator.DrainAsync(
            store,
            TimeSpan.FromSeconds(1),
            value => reported = value);

        Assert.Equal(DiagnosticsShutdownStatus.Failed, result.Status);
        Assert.Same(result, reported);
        Assert.Contains(nameof(IOException), result.Detail, StringComparison.Ordinal);
    }

    private static LocalDiagnosticEvent CreateEvent(int index) =>
        new(
            DateTimeOffset.UtcNow,
            "event",
            $"shutdown-{index:D3}",
            new Dictionary<string, string>());

    private sealed class ControlledStore : ILocalDiagnosticsStore
    {
        private readonly Exception? _failure;

        public ControlledStore(Exception? failure = null)
        {
            _failure = failure;
        }

        public TaskCompletionSource Drain { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool TryAppend(LocalDiagnosticEvent entry) => true;
        public Task AppendAsync(LocalDiagnosticEvent entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<LocalDiagnosticEvent>> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LocalDiagnosticEvent>>([]);
        public Task ClearAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<long> GetSizeAsync(CancellationToken cancellationToken = default) => Task.FromResult(0L);
        public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ShutdownAsync(CancellationToken cancellationToken = default) =>
            _failure is null ? Drain.Task : Task.FromException(_failure);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

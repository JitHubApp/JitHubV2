using System.Collections.Concurrent;
using System.Windows.Input;
using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class ModalOperationGateTests
{
    [Fact]
    public void RapidOpenStorm_AdmitsExactlyOneOperation()
    {
        var gate = new ModalOperationGate();
        var leases = new ConcurrentBag<long>();

        Parallel.For(0, 128, _ =>
        {
            if (gate.TryBegin(callback: null, out long lease))
            {
                leases.Add(lease);
            }
        });

        Assert.Single(leases);
        Assert.True(gate.IsOpen);
    }

    [Fact]
    public void RejectedOpen_DoesNotReplaceOriginalCallback()
    {
        var gate = new ModalOperationGate();
        var original = new TestCommand();
        var rejected = new TestCommand();

        Assert.True(gate.TryBegin(original, out _));
        Assert.False(gate.TryBegin(rejected, out _));
        Assert.True(gate.TryComplete(out ICommand? callback, out _));

        Assert.Same(original, callback);
        Assert.False(gate.IsOpen);
    }

    [Fact]
    public void Completion_IsIdempotent()
    {
        var gate = new ModalOperationGate();

        Assert.True(gate.TryBegin(callback: null, out _));
        Assert.True(gate.TryComplete(out _, out _));
        Assert.False(gate.TryComplete(out ICommand? callback, out long lease));
        Assert.Null(callback);
        Assert.Equal(0, lease);
    }

    [Fact]
    public void Abort_OnlyReleasesItsOwnLease()
    {
        var gate = new ModalOperationGate();

        Assert.True(gate.TryBegin(callback: null, out long lease));
        gate.Abort(lease + 1);
        Assert.True(gate.IsOpen);

        gate.Abort(lease);
        Assert.False(gate.IsOpen);
    }

    [Fact]
    public void FailedCloseRestore_DoesNotReplaceANewerOperation()
    {
        var gate = new ModalOperationGate();
        var original = new TestCommand();
        var newer = new TestCommand();

        Assert.True(gate.TryBegin(original, out _));
        Assert.True(gate.TryComplete(out ICommand? callback, out long lease));
        Assert.True(gate.TryBegin(newer, out _));

        gate.RestoreAfterCloseFailure(lease, callback);
        Assert.True(gate.TryComplete(out ICommand? current, out _));
        Assert.Same(newer, current);
    }

    [Fact]
    public void FailedCloseRestore_DoesNotResurrectAfterNewerOperationCompleted()
    {
        var gate = new ModalOperationGate();
        var original = new TestCommand();
        var newer = new TestCommand();

        Assert.True(gate.TryBegin(original, out _));
        Assert.True(gate.TryComplete(out ICommand? callback, out long lease));
        Assert.True(gate.TryBegin(newer, out _));
        Assert.True(gate.TryComplete(out _, out _));

        gate.RestoreAfterCloseFailure(lease, callback);

        Assert.False(gate.IsOpen);
        Assert.True(gate.TryBegin(callback: null, out _));
    }

    [Fact]
    public void OwnerScopedCompletion_CannotCloseNewerOperation()
    {
        var gate = new ModalOperationGate();
        var first = new TestCommand();
        var second = new TestCommand();

        Assert.True(gate.TryBegin(first, out long firstLease));
        Assert.True(gate.TryComplete(firstLease, out ICommand? firstCallback));
        Assert.Same(first, firstCallback);
        Assert.True(gate.TryBegin(second, out long secondLease));

        Assert.False(gate.TryComplete(firstLease, out _));
        Assert.True(gate.IsOpen);
        Assert.True(gate.TryComplete(secondLease, out ICommand? secondCallback));
        Assert.Same(second, secondCallback);
    }

    [Fact]
    public void HostReset_DropsStaleOperationAndCallback()
    {
        var gate = new ModalOperationGate();
        var staleCallback = new TestCommand();

        Assert.True(gate.TryBegin(staleCallback, out long staleLease));
        gate.Reset();

        Assert.False(gate.IsOpen);
        gate.Abort(staleLease);
        Assert.True(gate.TryBegin(callback: null, out _));
        Assert.True(gate.TryComplete(out ICommand? callback, out _));
        Assert.Null(callback);
    }

    [Fact]
    public void ShellDismissal_RoutesThroughModalServiceGate()
    {
        string shellPage = File.ReadAllText(FindRepositoryFile(
            "JitHub.WinUI",
            "Views",
            "Pages",
            "ShellPage.xaml.cs"));
        string shellViewModel = File.ReadAllText(FindRepositoryFile(
            "JitHub.WinUI",
            "ViewModels",
            "Pages",
            "ShellPageViewModel.cs"));

        Assert.Contains("ViewModel.RequestCloseModal();", shellPage, StringComparison.Ordinal);
        Assert.DoesNotContain("ViewModel.CloseModalWithControl();", shellPage, StringComparison.Ordinal);
        Assert.Contains("public void RequestCloseModal()", shellViewModel, StringComparison.Ordinal);
        Assert.Contains("_modalService.Close();", shellViewModel, StringComparison.Ordinal);
    }

    private static string FindRepositoryFile(params string[] segments)
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            string candidate = Path.Combine([current.FullName, .. segments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException($"Could not locate {Path.Combine(segments)}.");
    }

    private sealed class TestCommand : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter)
        {
        }
    }
}

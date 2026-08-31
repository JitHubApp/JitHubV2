using System.Collections.Concurrent;
using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class DialogSubmissionGateTests
{
    [Fact]
    public void RapidPrimaryInvocation_AdmitsExactlyOneMutation()
    {
        DialogSubmissionGate gate = new();
        ConcurrentBag<int> admitted = [];

        Parallel.For(0, 256, index =>
        {
            if (gate.TryBegin())
            {
                admitted.Add(index);
            }
        });

        Assert.Single(admitted);
        Assert.True(gate.IsSubmitting);
    }

    [Fact]
    public void FailedMutation_CanReleaseForRetry()
    {
        DialogSubmissionGate gate = new();
        Assert.True(gate.TryBegin());
        Assert.False(gate.TryBegin());

        gate.Complete();

        Assert.False(gate.IsSubmitting);
        Assert.True(gate.TryBegin());
    }

    [Fact]
    public async Task AsyncMutation_BlocksRapidDuplicateUntilOperationCompletes()
    {
        DialogSubmissionGate gate = new();
        TaskCompletionSource operationEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseOperation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int mutationCount = 0;

        async Task<bool> TryMutateAsync()
        {
            if (!gate.TryBegin())
            {
                return false;
            }

            try
            {
                Interlocked.Increment(ref mutationCount);
                operationEntered.TrySetResult();
                await releaseOperation.Task;
                return true;
            }
            finally
            {
                gate.Complete();
            }
        }

        Task<bool> first = TryMutateAsync();
        await operationEntered.Task;
        Task<bool>[] duplicates = Enumerable.Range(0, 32)
            .Select(_ => TryMutateAsync())
            .ToArray();

        Assert.All(await Task.WhenAll(duplicates), Assert.False);
        Assert.Equal(1, Volatile.Read(ref mutationCount));

        releaseOperation.TrySetResult();
        Assert.True(await first);
        Assert.False(gate.IsSubmitting);

        Assert.True(await TryMutateAsync());
        Assert.Equal(2, Volatile.Read(ref mutationCount));
    }
}

using System.Reflection;
using System.Threading;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Layout;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class WeakSnapshotPublicationDrainFenceTests
{
    [Fact]
    public void PublicationCannotBeClaimedUntilItsWorkerHasDrainedAndIsOneShot()
    {
        var fence = new WeakSnapshotPublicationDrainFence<object>();
        object snapshot = new();
        int deliveries = 0;
        object? deliveredSnapshot = null;
        fence.Reset(12);

        Assert.True(fence.TryStage(12, new WeakSnapshotPublication<object>(snapshot, published =>
        {
            deliveries++;
            deliveredSnapshot = published;
        })));
        Assert.True(fence.HasPendingPublication);
        Assert.False(fence.TryPublishAfterWorkDrains(12));
        Assert.Equal(0, deliveries);

        fence.MarkWorkDrained(12);
        Assert.True(fence.TryPublishAfterWorkDrains(12));
        Assert.Same(snapshot, deliveredSnapshot);
        Assert.Equal(1, deliveries);
        Assert.False(fence.HasPendingPublication);
        Assert.False(fence.TryPublishAfterWorkDrains(12));
        Assert.Equal(1, deliveries);
    }

    [Fact]
    public void DrainMayArriveBeforeStageWithoutLosingTheOneShotHandoff()
    {
        var fence = new WeakSnapshotPublicationDrainFence<object>();
        object snapshot = new();
        int deliveries = 0;
        fence.Reset(4);

        fence.MarkWorkDrained(4);
        Assert.True(fence.TryStage(4, new WeakSnapshotPublication<object>(snapshot, _ => deliveries++)));
        Assert.True(fence.TryPublishAfterWorkDrains(4));
        Assert.Equal(1, deliveries);
    }

    [Fact]
    public void ResetAndStaleGenerationsDiscardCallbacksWithoutPublishingThem()
    {
        var fence = new WeakSnapshotPublicationDrainFence<object>();
        object staleSnapshot = new();
        object currentSnapshot = new();
        int staleDeliveries = 0;
        int currentDeliveries = 0;
        fence.Reset(1);

        Assert.True(fence.TryStage(1, new WeakSnapshotPublication<object>(
            staleSnapshot, _ => staleDeliveries++)));
        fence.Reset(2);
        Assert.False(fence.HasPendingPublication);
        fence.MarkWorkDrained(1);
        Assert.False(fence.TryPublishAfterWorkDrains(1));
        Assert.False(fence.TryStage(1, new WeakSnapshotPublication<object>(
            staleSnapshot, _ => staleDeliveries++)));

        Assert.True(fence.TryStage(2, new WeakSnapshotPublication<object>(
            currentSnapshot, _ => currentDeliveries++)));
        fence.MarkWorkDrained(2);
        Assert.True(fence.TryPublishAfterWorkDrains(2));
        Assert.Equal(0, staleDeliveries);
        Assert.Equal(1, currentDeliveries);
    }

    [Theory]
    [InlineData("<CreateLazyLayoutCompletionPublication>")]
    [InlineData("<CreateLazyLayoutCompletionDispatchCallback>")]
    public void DelayedLazyCallbacksCaptureOnlyWeakControlAndValueMetadata(string callbackPrefix)
    {
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags methods = BindingFlags.Instance | BindingFlags.Static |
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        Type control = typeof(MarkdownDocumentView).BaseType!;
        Type closure = Assert.Single(control.GetNestedTypes(BindingFlags.NonPublic),
            type => type.GetMethods(methods).Any(method =>
                method.Name.StartsWith(callbackPrefix, StringComparison.Ordinal)));

        FieldInfo[] captured = closure.GetFields(fields);
        Assert.Contains(captured, field => field.FieldType.IsGenericType &&
            field.FieldType.GetGenericTypeDefinition() == typeof(WeakReference<>));
        Assert.DoesNotContain(captured, field => field.FieldType == control ||
            field.FieldType == typeof(LayoutSnapshot) ||
            field.FieldType == typeof(CancellationTokenSource) ||
            field.FieldType == typeof(MarkdownRenderer.Layout.Boxes.ImageBox));
    }
}

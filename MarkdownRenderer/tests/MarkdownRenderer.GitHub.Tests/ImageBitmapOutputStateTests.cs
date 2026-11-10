using MarkdownRenderer.Layout.Internal.Images.BitmapOutput;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Which bitmap-output deferral fields are live ownership and which are inert
/// history, as judged by the <see cref="ImageBitmapOutputState.HasNoLiveWork"/>
/// gate that placement transfer consults.
/// </summary>
public sealed class ImageBitmapOutputStateTests
{
    [Fact]
    public void InertHistoryIsNeitherLiveWorkNorPristine()
    {
        Assert.True(default(ImageBitmapOutputState).IsPristine);

        ImageBitmapOutputState history = InertHistory();
        Assert.True(history.HasNoLiveWork);
        Assert.False(history.IsPristine);
    }

    [Theory]
    [InlineData(nameof(ImageBitmapOutputState.HasWaiter))]
    [InlineData(nameof(ImageBitmapOutputState.HasWaiterOwner))]
    [InlineData(nameof(ImageBitmapOutputState.HasImageCancellation))]
    [InlineData(nameof(ImageBitmapOutputState.HasSessionCancellation))]
    [InlineData(nameof(ImageBitmapOutputState.HasDemand))]
    [InlineData(nameof(ImageBitmapOutputState.RequiredBytes))]
    [InlineData(nameof(ImageBitmapOutputState.CapacityVersion))]
    [InlineData(nameof(ImageBitmapOutputState.WaiterSlotVersion))]
    [InlineData(nameof(ImageBitmapOutputState.DemandGeneration))]
    [InlineData(nameof(ImageBitmapOutputState.SvgGeneration))]
    [InlineData(nameof(ImageBitmapOutputState.Deferred))]
    [InlineData(nameof(ImageBitmapOutputState.NeedsTrim))]
    [InlineData(nameof(ImageBitmapOutputState.Registering))]
    [InlineData(nameof(ImageBitmapOutputState.RegistrationNeeded))]
    [InlineData(nameof(ImageBitmapOutputState.CapacityReady))]
    [InlineData(nameof(ImageBitmapOutputState.RetryQueued))]
    [InlineData(nameof(ImageBitmapOutputState.RetryInProgress))]
    [InlineData(nameof(ImageBitmapOutputState.RetryOfferPending))]
    [InlineData(nameof(ImageBitmapOutputState.RegistrationFailed))]
    [InlineData(nameof(ImageBitmapOutputState.CancellationRegistered))]
    public void EachLiveFieldAloneIsLiveWorkEvenOverInertHistory(string field)
    {
        ImageBitmapOutputState history = InertHistory();
        ImageBitmapOutputState live = field switch
        {
            nameof(ImageBitmapOutputState.HasWaiter) => history with { HasWaiter = true },
            nameof(ImageBitmapOutputState.HasWaiterOwner) => history with { HasWaiterOwner = true },
            nameof(ImageBitmapOutputState.HasImageCancellation) => history with { HasImageCancellation = true },
            nameof(ImageBitmapOutputState.HasSessionCancellation) => history with { HasSessionCancellation = true },
            nameof(ImageBitmapOutputState.HasDemand) => history with { HasDemand = true },
            nameof(ImageBitmapOutputState.RequiredBytes) => history with { RequiredBytes = 1 },
            nameof(ImageBitmapOutputState.CapacityVersion) => history with { CapacityVersion = 1 },
            nameof(ImageBitmapOutputState.WaiterSlotVersion) => history with { WaiterSlotVersion = 1 },
            nameof(ImageBitmapOutputState.DemandGeneration) => history with { DemandGeneration = 1 },
            nameof(ImageBitmapOutputState.SvgGeneration) => history with { SvgGeneration = 1 },
            nameof(ImageBitmapOutputState.Deferred) => history with { Deferred = true },
            nameof(ImageBitmapOutputState.NeedsTrim) => history with { NeedsTrim = true },
            nameof(ImageBitmapOutputState.Registering) => history with { Registering = true },
            nameof(ImageBitmapOutputState.RegistrationNeeded) => history with { RegistrationNeeded = true },
            nameof(ImageBitmapOutputState.CapacityReady) => history with { CapacityReady = true },
            nameof(ImageBitmapOutputState.RetryQueued) => history with { RetryQueued = true },
            nameof(ImageBitmapOutputState.RetryInProgress) => history with { RetryInProgress = true },
            nameof(ImageBitmapOutputState.RetryOfferPending) => history with { RetryOfferPending = true },
            nameof(ImageBitmapOutputState.RegistrationFailed) => history with { RegistrationFailed = true },
            nameof(ImageBitmapOutputState.CancellationRegistered) => history with { CancellationRegistered = true },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        Assert.False(live.HasNoLiveWork);
        Assert.False(live.IsPristine);
    }

    // Monotonic waiter epochs and cached visibility and tier hints that a
    // retired request leaves behind.
    private static ImageBitmapOutputState InertHistory() => default(ImageBitmapOutputState) with
    {
        WaiterEpoch = 11,
        RecoverySignaledEpoch = 7,
        Visible = true,
        Replacement = true,
        ExistingTile = true,
    };
}

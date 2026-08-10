using System;
using System.Runtime.InteropServices;
using JitHub.Services.Markdown;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class RuntimeEventSubscriptionTests
{
    [Fact]
    public void TryCreate_ComAvailabilityFailureReturnsNoSource()
    {
        object? source = RuntimeEventSubscription.TryCreate<object>(
            static () => throw new COMException("not found", unchecked((int)0x80070490)),
            "AccessibilitySettings");

        Assert.Null(source);
    }

    [Fact]
    public void TrySubscribe_ComAvailabilityFailureIsBestEffort()
    {
        bool subscribed = RuntimeEventSubscription.TrySubscribe(
            static () => throw new COMException("not found", unchecked((int)0x80070490)),
            "HighContrastChanged");

        Assert.False(subscribed);
    }

    [Fact]
    public void TrySubscribe_DoesNotSwallowUnrelatedExceptions()
    {
        InvalidOperationException failure = new("programming error");

        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() =>
            RuntimeEventSubscription.TrySubscribe(() => throw failure, "TextScaleFactorChanged")));
    }

    [Fact]
    public void TryUnsubscribe_IsConditionalAndContainsComTeardownFailure()
    {
        int calls = 0;
        RuntimeEventSubscription.TryUnsubscribe(() => calls++, wasSubscribed: false, "ColorValuesChanged");
        RuntimeEventSubscription.TryUnsubscribe(
            () =>
            {
                calls++;
                throw new COMException("source gone", unchecked((int)0x80070490));
            },
            wasSubscribed: true,
            "ColorValuesChanged");

        Assert.Equal(1, calls);
    }
}

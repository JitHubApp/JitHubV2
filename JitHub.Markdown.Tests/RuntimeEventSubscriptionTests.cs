using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Xunit;

namespace JitHub.Markdown.Tests;

public sealed class RuntimeEventSubscriptionTests
{
    private readonly List<string> _reports = [];

    [Fact]
    public void TryCreate_ComAvailabilityFailureReturnsNoSource()
    {
        object? source = CreateSubscription().TryCreate<object>(
            static () => throw new COMException("not found", unchecked((int)0x80070490)),
            "AccessibilitySettings");

        Assert.Null(source);
        Assert.Equal(["markdown-runtime-source"], _reports);
    }

    [Fact]
    public void TrySubscribe_ComAvailabilityFailureIsBestEffort()
    {
        bool subscribed = CreateSubscription().TrySubscribe(
            static () => throw new COMException("not found", unchecked((int)0x80070490)),
            "HighContrastChanged");

        Assert.False(subscribed);
        Assert.Equal(["markdown-runtime-event-subscribe"], _reports);
    }

    [Fact]
    public void TrySubscribe_DoesNotSwallowUnrelatedExceptions()
    {
        InvalidOperationException failure = new("programming error");

        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() =>
            CreateSubscription().TrySubscribe(() => throw failure, "TextScaleFactorChanged")));
        Assert.Empty(_reports);
    }

    [Fact]
    public void TryUnsubscribe_IsConditionalAndContainsComTeardownFailure()
    {
        RuntimeEventSubscription subscription = CreateSubscription();
        int calls = 0;
        subscription.TryUnsubscribe(() => calls++, wasSubscribed: false, "ColorValuesChanged");
        subscription.TryUnsubscribe(
            () =>
            {
                calls++;
                throw new COMException("source gone", unchecked((int)0x80070490));
            },
            wasSubscribed: true,
            "ColorValuesChanged");

        Assert.Equal(1, calls);
        Assert.Equal(["markdown-runtime-event-unsubscribe"], _reports);
    }

    [Fact]
    public void EachFailureKindIsReportedOncePerOwner()
    {
        RuntimeEventSubscription subscription = CreateSubscription();
        for (int attempt = 0; attempt < 3; attempt++)
        {
            Assert.False(subscription.TrySubscribe(
                static () => throw new COMException("not found", unchecked((int)0x80070490)),
                "PaletteChanged"));
            Assert.Null(subscription.TryCreate<object>(
                static () => throw new COMException("not found", unchecked((int)0x80070490)),
                "UISettings"));
        }

        Assert.Equal(["markdown-runtime-event-subscribe", "markdown-runtime-source"], _reports);

        // A second owner reports independently: the once-only state is not process-wide.
        Assert.False(CreateSubscription().TrySubscribe(
            static () => throw new COMException("not found", unchecked((int)0x80070490)),
            "PaletteChanged"));
        Assert.Equal(3, _reports.Count);
    }

    private RuntimeEventSubscription CreateSubscription() =>
        new((_, category) => _reports.Add(category));
}

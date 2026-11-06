using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using Xunit;
using Microsoft.UI.Xaml.Automation;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Checks that externally retained virtual peers do not own retired layout
/// projections. Source assertions are not a live WinUI/UIA integration test.
/// </summary>
public sealed class MarkdownAutomationPeerRetentionTests
{
    private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void ProjectionPayloadFieldsAreWeakAcrossVirtualPeerTypes()
    {
        AssertWeakPayloadField(typeof(MarkdownNodePeer), "_nodeReference", typeof(MarkdownSemanticNode));
        AssertWeakPayloadField(typeof(MarkdownBlockPeer), "_boxReference", typeof(InlineContainerBox));
        AssertWeakPayloadField(typeof(MarkdownLinkPeer), "_parentReference", typeof(MarkdownBlockPeer));
        AssertWeakPayloadField(typeof(MarkdownLinkPeer), "_runReference", typeof(LinkRun));
        AssertWeakPayloadField(typeof(MarkdownLinkedImagePeer), "_parentReference", typeof(MarkdownBlockPeer));
        AssertWeakPayloadField(typeof(MarkdownLinkedImagePeer), "_runReference", typeof(InlineImageRun));
        AssertWeakPayloadField(typeof(MarkdownCodeBlockCopyPeer), "_boxReference", typeof(CodeBlockBox));

        AssertNoStrongPayloadField(typeof(MarkdownNodePeer), typeof(MarkdownSemanticNode));
        AssertNoStrongPayloadField(typeof(MarkdownBlockPeer), typeof(InlineContainerBox));
        AssertNoStrongPayloadField(typeof(MarkdownLinkPeer), typeof(MarkdownBlockPeer));
        AssertNoStrongPayloadField(typeof(MarkdownLinkPeer), typeof(LinkRun));
        AssertNoStrongPayloadField(typeof(MarkdownLinkedImagePeer), typeof(MarkdownBlockPeer));
        AssertNoStrongPayloadField(typeof(MarkdownLinkedImagePeer), typeof(InlineImageRun));
        AssertNoStrongPayloadField(typeof(MarkdownCodeBlockCopyPeer), typeof(CodeBlockBox));
    }

    [Fact]
    public void CapturedPayloadDoesNotStayAliveThroughItsWeakReference()
    {
        WeakReference<object> payload = CreateUnrootedPayload();
        for (int attempt = 0; attempt < 3 && IsPayloadAlive(payload); attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.False(IsPayloadAlive(payload));
        Assert.Throws<ElementNotAvailableException>(
            () => MarkdownAutomationPeerLifetime.GetPayloadOrThrow(payload));
    }

    private static void AssertWeakPayloadField(Type peerType, string fieldName, Type payloadType)
    {
        FieldInfo? field = peerType.GetField(fieldName, InstanceFields);
        Assert.NotNull(field);
        Assert.Equal(typeof(WeakReference<>).MakeGenericType(payloadType), field!.FieldType);
    }

    private static void AssertNoStrongPayloadField(Type peerType, Type payloadType)
    {
        foreach (FieldInfo field in peerType.GetFields(InstanceFields))
        {
            Assert.False(
                payloadType.IsAssignableFrom(field.FieldType),
                $"{peerType.Name}.{field.Name} must not strongly retain {payloadType.Name}.");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<object> CreateUnrootedPayload()
    {
        var payload = new object();
        return new WeakReference<object>(payload);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsPayloadAlive(WeakReference<object> payload) => payload.TryGetTarget(out _);
}

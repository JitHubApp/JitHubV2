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

    [Fact]
    public void ExistingRootPeerReconcilesEveryPublishedAndClearedSnapshot()
    {
        string control = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Controls", "MarkdownRendererControl.cs");
        string automationPeer = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Accessibility", "MarkdownAutomationPeer.cs");
        string nodePeer = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Accessibility", "MarkdownNodePeer.cs");
        string linkedImagePeer = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Accessibility", "MarkdownLinkedImagePeer.cs");

        Assert.Equal(3, CountOccurrences(control, "NotifyAutomationSnapshotChanged();"));
        string reconciliation = GetMemberBody(control, "NotifyAutomationSnapshotChanged()");
        Assert.Contains("FrameworkElementAutomationPeer.FromElement(this)", reconciliation, StringComparison.Ordinal);
        Assert.DoesNotContain("CreatePeerForElement", reconciliation, StringComparison.Ordinal);
        Assert.Contains(
            "_lifetime.TryGetCurrentPayload(_nodeReference",
            GetMemberBody(nodePeer, "NotifyImageStatusChanged()"),
            StringComparison.Ordinal);
        Assert.Contains(
            "_lifetime.TryGetCurrentPayload(_runReference",
            GetMemberBody(linkedImagePeer, "NotifyImageStatusChanged()"),
            StringComparison.Ordinal);

        string notification = GetMemberBody(automationPeer, "NotifySnapshotChanged()");
        Assert.Contains("GetSemanticDocumentOrNull()", notification, StringComparison.Ordinal);
        Assert.Contains("NotifySnapshotChanged();", GetMemberBody(automationPeer, "NotifyDocumentChanged()"), StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(
            GetMemberBody(automationPeer, "GetSemanticDocumentOrNull(LayoutSnapshot? snap)"),
            "_nodePeerCache = new Dictionary<MarkdownSemanticNode, MarkdownNodePeer>();"));
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

    private static string GetMemberBody(string source, string memberSignature)
    {
        int member = source.IndexOf(memberSignature, StringComparison.Ordinal);
        Assert.True(member >= 0, $"Could not find member '{memberSignature}'.");
        int bodyStart = source.IndexOf('{', member + memberSignature.Length);
        Assert.True(bodyStart >= 0, $"Could not find a body for '{memberSignature}'.");

        int depth = 0;
        for (int index = bodyStart; index < source.Length; index++)
        {
            if (source[index] == '{')
                depth++;
            else if (source[index] == '}' && --depth == 0)
                return source[bodyStart..(index + 1)];
        }

        throw new InvalidOperationException($"Could not find the end of '{memberSignature}'.");
    }

    private static int CountOccurrences(string source, string value)
    {
        int count = 0;
        for (int offset = 0;
             (offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0;
             offset += value.Length)
        {
            count++;
        }

        return count;
    }

    private static string ReadRepositorySource(params string[] pathParts)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            string path = directory.FullName;
            foreach (string part in pathParts)
                path = Path.Combine(path, part);
            if (File.Exists(path))
                return File.ReadAllText(path);
        }

        throw new FileNotFoundException(
            $"Could not locate repository source '{string.Join(Path.DirectorySeparatorChar, pathParts)}'.");
    }
}

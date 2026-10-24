using System.IO;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class PointerPostInteractionTests
{
    [Fact]
    public void TapAndReleaseStageClearCaptureAndDetachedLinkIntent()
    {
        string control = ReadSource("MarkdownRendererControl.cs");
        string tap = Region(
            control,
            "private void ProcessTapped(ref PointerInput e)",
            "private void ProcessDoubleTapped(ref PointerInput e)");
        string release = Region(
            control,
            "private void ProcessPointerReleased(ref PointerInput e)",
            "private bool TryCreateLinkActivationIntent(");

        Assert.Contains("e.InteractionSnapshot is not { } snapshot", tap, StringComparison.Ordinal);
        Assert.Contains("e.ClearSelectionAfterInteraction = true;", tap, StringComparison.Ordinal);
        Assert.Contains("TryCreateLinkActivationIntent(", tap, StringComparison.Ordinal);
        Assert.Contains("e.SetLinkActivation(intent);", tap, StringComparison.Ordinal);
        Assert.DoesNotContain("ClearSelectionForExternalInteraction(", tap, StringComparison.Ordinal);
        Assert.DoesNotContain("ActivateLinkIntent(", tap, StringComparison.Ordinal);

        Assert.Contains("e.ReleaseCanvasCaptureAfterInteraction = true;", release, StringComparison.Ordinal);
        Assert.Contains("e.ResumeRebuildAfterInteraction = true;", release, StringComparison.Ordinal);
        Assert.Contains("e.InteractionSnapshot is not { } snapshot", release, StringComparison.Ordinal);
        Assert.Contains("e.LinkModifiers", release, StringComparison.Ordinal);
        Assert.Contains("e.SetLinkActivation(intent);", release, StringComparison.Ordinal);
        Assert.DoesNotContain("ReleasePointerCapture(", release, StringComparison.Ordinal);
        Assert.DoesNotContain("ActivateLinkAtPoint(", release, StringComparison.Ordinal);
    }

    [Fact]
    public void HorizontalOverflowReleaseAndCancelAlsoDeferReentrantWork()
    {
        string control = ReadSource("MarkdownRendererControl.cs");
        string release = Region(
            control,
            "private bool HandleHorizontalOverflowPointerReleased(ref PointerInput e)",
            "private bool HandleHorizontalOverflowPointerCanceled(ref PointerInput e)");
        string cancel = Region(
            control,
            "private bool HandleHorizontalOverflowPointerCanceled(ref PointerInput e)",
            "private static void SetHorizontalOffsetFromTrackPoint(");

        Assert.Contains("e.ReleaseCanvasCaptureAfterInteraction = true;", release, StringComparison.Ordinal);
        Assert.Contains("e.ResumeRebuildAfterInteraction = true;", release, StringComparison.Ordinal);
        Assert.DoesNotContain("ReleasePointerCapture(", release, StringComparison.Ordinal);
        Assert.DoesNotContain("ResumePendingRebuildAfterPointerInteraction();", release, StringComparison.Ordinal);
        Assert.Contains("e.ResumeRebuildAfterInteraction = true;", cancel, StringComparison.Ordinal);
        Assert.DoesNotContain("ResumePendingRebuildAfterPointerInteraction();", cancel, StringComparison.Ordinal);
    }

    [Fact]
    public void DirectAndDeferredDispatchClearSnapshotAliasBeforeRunningPostWork()
    {
        string pointer = ReadSource("MarkdownRendererControl.PointerInput.cs");
        string direct = Region(
            pointer,
            "private void DispatchPointerInput(",
            "private DispatcherQueueTimer CreatePointerRetryTimer()");
        string gesture = Region(
            pointer,
            "private bool DispatchRecognizedGesture(",
            "private void DispatchPointerInput(");
        string timer = Region(
            pointer,
            "private void OnPointerRetryTimer(",
            "private void FinishDeferredPointerInputDrain(");

        AssertOrdered(direct,
            "input.InteractionSnapshot = snapshot;",
            "ProcessPointerInput(ref input);",
            "input.InteractionSnapshot = null;",
            "snapshot.EndInteraction();",
            "CompletePointerPostInteraction(");
        AssertOrdered(gesture,
            "input.InteractionSnapshot = snapshot;",
            "ProcessPointerInput(ref input);",
            "input.InteractionSnapshot = null;",
            "snapshot.EndInteraction();",
            "CompletePointerPostInteraction(");
        AssertOrdered(timer,
            "input.InteractionSnapshot = snapshot;",
            "ProcessPointerInput(ref input);",
            "input.InteractionSnapshot = null;",
            "snapshot.EndInteraction();",
            "CompletePointerPostInteraction(");
        Assert.Contains("if (input.ShowContextMenuAfterInteraction || input.HasPostInteractionWork)", timer, StringComparison.Ordinal);
        Assert.Contains("continue;", timer, StringComparison.Ordinal);
    }

    [Fact]
    public void PostWorkFencesCaptureClearAndActivationAgainstTheExactSnapshotTuple()
    {
        string pointer = ReadSource("MarkdownRendererControl.PointerInput.cs");
        string completion = Region(
            pointer,
            "private bool CompletePointerPostInteraction(",
            "private bool IsCurrentPointerInteraction(");
        string current = Region(
            pointer,
            "private bool IsCurrentPointerInteraction(",
            "private void ProcessPointerInput(");

        AssertOrdered(completion,
            "if (!IsCurrentPointerInteraction(snapshot, snapshotGeneration, layoutRevision) ||",
            "UIElement captureTarget = input.InteractionCanvas!;",
            "captureTarget.ReleasePointerCapture(input.Pointer);",
            "capturedPointer.PointerId == input.ReleasePointerIdAfterInteraction",
            "_contextCaptureReleaseInFlightId = input.ReleasePointerIdAfterInteraction;",
            "captureTarget.ReleasePointerCapture(capturedPointer);",
            "_contextCaptureReleaseInFlightId = 0;",
            "if (!IsCurrentPointerInteraction(snapshot, snapshotGeneration, layoutRevision) ||",
            "ResumePendingRebuildAfterPointerInteraction();",
            "ClearSelectionForExternalInteraction(resetClickTracking: false);",
            "UpdateSelectionAdornerViewport();",
            "input.TryGetLinkActivation(out LinkActivationIntent activation)",
            "ActivateLinkIntent(");
        Assert.Contains("_drainingPointerInput = true;", completion, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(_canvas, capturedCanvas)", completion, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(_canvas, input.InteractionCanvas)", completion, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(_snapshot, snapshot)", current, StringComparison.Ordinal);
        Assert.Contains("snapshotGeneration == Volatile.Read(ref _snapshotGeneration)", current, StringComparison.Ordinal);
        Assert.Contains("snapshot.LayoutRevision == layoutRevision", current, StringComparison.Ordinal);
    }

    [Fact]
    public void LinkModifiersAreCapturedAtDeliveryAndDetachedIntentHasNoLayoutReferences()
    {
        string pointer = ReadSource("MarkdownRendererControl.PointerInput.cs");
        string control = ReadSource("MarkdownRendererControl.cs");
        string routed = Region(
            pointer,
            "private void DispatchPointerInput(",
            "private DispatcherQueueTimer CreatePointerRetryTimer()");
        string gesture = Region(
            pointer,
            "private bool DispatchRecognizedGesture(",
            "private void DispatchPointerInput(");
        string tap = Region(
            control,
            "private void ProcessTapped(ref PointerInput e)",
            "private void ProcessDoubleTapped(ref PointerInput e)");
        string release = Region(
            control,
            "private void ProcessPointerReleased(ref PointerInput e)",
            "private bool TryCreateLinkActivationIntent(");
        string intent = Region(
            control,
            "private readonly struct LinkActivationIntent",
            "/// <summary>Event data for markdown link activation.</summary>");
        string mapping = Region(
            control,
            "private static MarkdownInputModifiers MapInputModifiers(",
            "private static MarkdownLinkDisposition GetRequestedDisposition(");

        Assert.Contains("LinkModifiers = MapInputModifiers(args.KeyModifiers)", routed, StringComparison.Ordinal);
        Assert.Contains("LinkModifiers = GetCurrentInputModifiers()", gesture, StringComparison.Ordinal);
        Assert.Contains("e.LinkModifiers", tap, StringComparison.Ordinal);
        Assert.Contains("e.LinkModifiers", release, StringComparison.Ordinal);
        Assert.DoesNotContain("GetCurrentInputModifiers()", tap, StringComparison.Ordinal);
        Assert.DoesNotContain("GetCurrentInputModifiers()", release, StringComparison.Ordinal);
        Assert.Contains("if ((modifiers & VirtualKeyModifiers.Control) != 0)", mapping, StringComparison.Ordinal);
        Assert.Contains("if ((modifiers & VirtualKeyModifiers.Shift) != 0)", mapping, StringComparison.Ordinal);
        Assert.Contains("if ((modifiers & VirtualKeyModifiers.Menu) != 0)", mapping, StringComparison.Ordinal);
        Assert.Contains("if ((modifiers & VirtualKeyModifiers.Windows) != 0)", mapping, StringComparison.Ordinal);
        Assert.Contains("SourceSpan SourceSpan", intent, StringComparison.Ordinal);
        Assert.Contains("string? DisclosureId", intent, StringComparison.Ordinal);
        Assert.DoesNotContain("LayoutSnapshot", intent, StringComparison.Ordinal);
        Assert.DoesNotContain("InlineRun", intent, StringComparison.Ordinal);
        Assert.DoesNotContain("BlockBox", intent, StringComparison.Ordinal);
    }

    [Fact]
    public void PointerCommandAndFallbackCallbacksRecheckTheLeasedTuple()
    {
        string control = ReadSource("MarkdownRendererControl.cs");
        string target = Region(
            control,
            "private bool ActivateLinkTarget(",
            "private void ResumePendingRebuildAfterPointerInteraction()");
        string activation = Region(
            control,
            "private bool ActivateLinkIntent(",
            "private bool ActivateLinkTarget(");

        Assert.Contains("bool IsCurrent() => IsCurrentPointerInteraction(", activation, StringComparison.Ordinal);
        Assert.Contains("intent.Modifiers,\n            IsCurrent)", activation, StringComparison.Ordinal);
        Assert.Contains("MarkdownLinkActivationPolicy.Evaluate(\n                    inputKind,", target, StringComparison.Ordinal);
        Assert.Contains("link.Action,\n                    isCurrent)", target, StringComparison.Ordinal);
        AssertOrdered(target,
            "MarkdownLinkActivationPolicy.Evaluate(",
            "if (isCurrent is not null && !isCurrent())\n                return false;\n            decision = MarkdownLinkActivationPolicy.CreateHostFallback(inputKind);",
            "if (isCurrent is not null && !isCurrent())\n            return false;\n        if (decision.IsHandled)",
            "LinkClick?.Invoke(");

        string dispatcher = ReadSource("MarkdownTargetCommand.cs");
        string execute = Region(
            dispatcher,
            "private static MarkdownTargetCommandDispatchResult ExecuteCore(",
            "/// <summary>\n/// Centralizes modality-neutral link routing");
        AssertOrdered(execute,
            "provider?.GetCommand(context)",
            "if (isCurrent is not null && !isCurrent())",
            "command.CanExecute(context)",
            "if (isCurrent is not null && !isCurrent())",
            "command.Execute(context)");
    }

    private static void AssertOrdered(string source, params string[] fragments)
    {
        int previous = -1;
        foreach (string fragment in fragments)
        {
            int current = source.IndexOf(fragment, previous + 1, StringComparison.Ordinal);
            Assert.True(current > previous, $"Expected '{fragment}' after the preceding fenced operation.");
            previous = current;
        }
    }

    private static string Region(string source, string start, string end)
    {
        int startIndex = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, $"Could not find source member '{start}'.");
        int endIndex = source.IndexOf(end, startIndex + start.Length, StringComparison.Ordinal);
        Assert.True(endIndex > startIndex, $"Could not find the end of source member '{start}'.");
        return source[startIndex..endIndex];
    }

    private static string ReadSource(string fileName)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            string[] candidates =
            [
                Path.Combine(directory.FullName, "MarkdownRenderer", fileName),
                Path.Combine(directory.FullName, "MarkdownRenderer", "Controls", fileName),
            ];

            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate))
                    return File.ReadAllText(candidate).Replace("\r\n", "\n", StringComparison.Ordinal);
            }
        }

        throw new FileNotFoundException($"Could not locate {fileName} from the test output.");
    }
}

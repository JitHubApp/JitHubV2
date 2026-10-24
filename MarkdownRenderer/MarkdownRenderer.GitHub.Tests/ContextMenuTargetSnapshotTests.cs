using System;
using System.IO;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Source-wiring checks for context-menu hit testing, whose WinUI flyout cannot
/// be created in the headless contract suite. Bitmap-lease cloning and
/// retirement are covered by the runtime test in ImmutablePayloadStorageTests.
/// </summary>
public sealed class ContextMenuTargetSnapshotTests
{
    [Fact]
    public void HitTestingUsesNonblockingSnapshotLeaseAndRevalidatesBeforeReturning()
    {
        string source = ReadControl();
        string show = Slice(
            source,
            "private bool ShowSelectionContextMenu(",
            "private void OnSelectionContextMenuClosed(");
        string find = Slice(
            source,
            "private bool FindContextMenuTarget(",
            "private static bool PopulateContextMenuTarget(");

        int capture = show.IndexOf("FindContextMenuTarget(", StringComparison.Ordinal);
        int provider = show.IndexOf("CanExecuteSelectionCommandOrDefault(", StringComparison.Ordinal);
        Assert.True(capture >= 0 && provider > capture);
        Assert.Contains("requiredSnapshot", show, StringComparison.Ordinal);
        Assert.Contains("requiredSnapshotGeneration", show, StringComparison.Ordinal);
        Assert.Contains("requiredLayoutRevision", show, StringComparison.Ordinal);
        Assert.Contains("expectedSnapshotGeneration", show, StringComparison.Ordinal);
        Assert.Contains("expectedLayoutRevision", show, StringComparison.Ordinal);
        Assert.Contains("snapshot.TryBeginInteraction()", find, StringComparison.Ordinal);
        Assert.Contains("snapshot.EndInteraction()", find, StringComparison.Ordinal);
        Assert.Contains("finally", find, StringComparison.Ordinal);
        Assert.Contains("IsCurrentContextMenuSnapshot(", find, StringComparison.Ordinal);
        Assert.Contains("expectedSnapshotGeneration", find, StringComparison.Ordinal);
        Assert.Contains("expectedLayoutRevision", find, StringComparison.Ordinal);
        Assert.DoesNotContain("CommandProvider", find, StringComparison.Ordinal);
        Assert.DoesNotContain("CanExecute", find, StringComparison.Ordinal);
        Assert.DoesNotContain("Clipboard", find, StringComparison.Ordinal);
        Assert.DoesNotContain("await ", find, StringComparison.Ordinal);
        Assert.Contains("target = new ContextMenuTarget();", find, StringComparison.Ordinal);
        Assert.Contains("menu.Items.Add(selectAllItem)", show, StringComparison.Ordinal);
    }

    [Fact]
    public void PointerMenuKeepsTheGestureSnapshotRevisionThroughFinalShowAtFence()
    {
        string control = ReadControl();
        string pointerInput = ReadPointerInput();
        string recognized = Slice(
            pointerInput,
            "private bool DispatchRecognizedGesture(",
            "private void DispatchPointerInput(");
        string retry = Slice(
            pointerInput,
            "private void OnPointerRetryTimer(",
            "private void FinishDeferredPointerInputDrain(");
        string complete = Slice(
            pointerInput,
            "private bool CompleteGestureAfterSnapshotInteraction(",
            "private void ProcessPointerInput(");
        string show = Slice(
            control,
            "private bool ShowSelectionContextMenu(",
            "private void OnSelectionContextMenuClosed(");
        string find = Slice(
            control,
            "private bool FindContextMenuTarget(",
            "private static bool PopulateContextMenuTarget(");
        string tupleGuard = Slice(
            control,
            "private bool IsCurrentContextMenuSnapshot(",
            "private bool FindContextMenuTarget(");

        Assert.Contains("internal long GestureLayoutRevision", pointerInput, StringComparison.Ordinal);

        int directLease = recognized.IndexOf("snapshot.TryBeginInteraction()", StringComparison.Ordinal);
        int directRevision = recognized.IndexOf("long layoutRevision = snapshot.LayoutRevision", StringComparison.Ordinal);
        int directSave = recognized.IndexOf("input.GestureLayoutRevision = layoutRevision", StringComparison.Ordinal);
        int directRelease = recognized.IndexOf("snapshot.EndInteraction()", StringComparison.Ordinal);
        int directComplete = recognized.IndexOf("CompleteGestureAfterSnapshotInteraction(", StringComparison.Ordinal);
        Assert.True(directLease >= 0 && directRevision > directLease && directSave > directRevision);
        Assert.True(directRelease > directSave && directComplete > directRelease);

        int deferredRevision = retry.IndexOf("long layoutRevision = snapshot.LayoutRevision", StringComparison.Ordinal);
        int deferredSave = retry.IndexOf("input.GestureLayoutRevision = layoutRevision", StringComparison.Ordinal);
        int deferredProcess = retry.IndexOf("ProcessPointerInput(ref input)", StringComparison.Ordinal);
        int deferredRelease = retry.IndexOf("snapshot.EndInteraction()", StringComparison.Ordinal);
        int deferredComplete = retry.IndexOf("CompleteGestureAfterSnapshotInteraction(", StringComparison.Ordinal);
        Assert.True(deferredRevision >= 0 && deferredSave > deferredRevision && deferredProcess > deferredSave);
        Assert.True(deferredRelease > deferredProcess && deferredComplete > deferredRelease);

        Assert.Contains("input.GestureLayoutRevision != layoutRevision", complete, StringComparison.Ordinal);
        Assert.Contains("snapshot.LayoutRevision != layoutRevision", complete, StringComparison.Ordinal);
        Assert.Contains("requiredSnapshot: snapshot", complete, StringComparison.Ordinal);
        Assert.Contains("requiredSnapshotGeneration: snapshotGeneration", complete, StringComparison.Ordinal);
        Assert.Contains("requiredLayoutRevision: layoutRevision", complete, StringComparison.Ordinal);

        int showInitialFence = show.IndexOf("IsCurrentContextMenuSnapshot(", StringComparison.Ordinal);
        int findCall = show.IndexOf("FindContextMenuTarget(", StringComparison.Ordinal);
        int finalFence = show.LastIndexOf("IsCurrentContextMenuSnapshot(", StringComparison.Ordinal);
        int showAt = show.IndexOf("menu.ShowAt(canvas, placementPoint)", StringComparison.Ordinal);
        Assert.True(showInitialFence >= 0 && findCall > showInitialFence);
        Assert.Contains("expectedSnapshot", show, StringComparison.Ordinal);
        Assert.Contains("expectedSnapshotGeneration", show, StringComparison.Ordinal);
        Assert.Contains("expectedLayoutRevision", show, StringComparison.Ordinal);
        Assert.True(finalFence > findCall && showAt > finalFence);
        Assert.Contains(
            "DisposeContextMenuTarget(target);\n                OnSelectionContextMenuClosed(menu, touchSelection);\n                return false;",
            show,
            StringComparison.Ordinal);

        int beforeAcquire = find.IndexOf("IsCurrentContextMenuSnapshot(", StringComparison.Ordinal);
        int acquire = find.IndexOf("snapshot.TryBeginInteraction()", StringComparison.Ordinal);
        int underLease = find.IndexOf("if (IsCurrentContextMenuSnapshot(", acquire, StringComparison.Ordinal);
        int release = find.IndexOf("snapshot.EndInteraction()", StringComparison.Ordinal);
        int afterRelease = find.LastIndexOf("IsCurrentContextMenuSnapshot(", StringComparison.Ordinal);
        Assert.True(beforeAcquire >= 0 && acquire > beforeAcquire && underLease > acquire);
        Assert.True(release > underLease && afterRelease > release);
        Assert.Contains("ReferenceEquals(snapshot, _snapshot)", tupleGuard, StringComparison.Ordinal);
        Assert.Contains("Volatile.Read(ref _snapshotGeneration)", tupleGuard, StringComparison.Ordinal);
        Assert.Contains("snapshot.LayoutRevision == layoutRevision", tupleGuard, StringComparison.Ordinal);
    }

    [Fact]
    public void HitTestMaterializesTargetPayloadWithoutExternalCallbacksOrLiveBoxes()
    {
        string source = ReadControl();
        string populate = Slice(
            source,
            "private static bool PopulateContextMenuTarget(",
            "private static void PopulateInlineContextMenuTarget(");
        string inline = Slice(
            source,
            "private static void PopulateInlineContextMenuTarget(",
            "private static SourceSpan GetSourceRange(");
        string target = Slice(
            source,
            "private sealed class ContextMenuTarget : IDisposable",
            "internal readonly struct LinkTarget");

        foreach (string capture in new[]
        {
            "target.CodeText = code.CodeText",
            "target.CodeLanguage = code.CodeLanguage",
            "target.CodeSourceRange = GetSourceRange(code)",
            "target.ImageUrl = image.Url",
            "target.SetImageBitmapLease(image.AcquireBitmapLease())",
            "target.TableSnapshot = BuildTableClipboardSnapshot(table)",
        })
        {
            Assert.Contains(capture, populate, StringComparison.Ordinal);
        }
        Assert.Contains("target.ImageUrl = image.Image.Url", inline, StringComparison.Ordinal);
        Assert.Contains("target.LinkUrl = link.Url", inline, StringComparison.Ordinal);
        Assert.DoesNotContain("CommandProvider", populate, StringComparison.Ordinal);
        Assert.DoesNotContain("Clipboard.SetContent", populate, StringComparison.Ordinal);
        Assert.DoesNotContain("Clipboard.Flush", populate, StringComparison.Ordinal);
        Assert.DoesNotContain("CopyTableTargetToClipboard", populate, StringComparison.Ordinal);
        Assert.DoesNotContain("DataPackage", populate, StringComparison.Ordinal);
        Assert.DoesNotContain("Dispatcher", populate, StringComparison.Ordinal);

        foreach (string liveType in new[]
        {
            "BlockBox", "InlineRun", "ImageBox", "CodeBlockBox", "TableBox", "LayoutSnapshot",
        })
        {
            Assert.DoesNotContain(liveType + "?", target, StringComparison.Ordinal);
            Assert.DoesNotContain(liveType + " ", target, StringComparison.Ordinal);
        }
        Assert.Contains("MarkdownTableClipboardSnapshot? TableSnapshot", target, StringComparison.Ordinal);
        Assert.Contains("ImageBitmapLease?.TryClone()", target, StringComparison.Ordinal);
        Assert.Contains("Interlocked.Exchange(ref _imageBitmapLease, null)?.Dispose()", target, StringComparison.Ordinal);
    }

    [Fact]
    public void TableClipboardFormattingIsDeferredUntilBuiltInCopyRunsOffThread()
    {
        string source = ReadControl();
        string find = Slice(
            source,
            "private bool FindContextMenuTarget(",
            "private static bool PopulateContextMenuTarget(");
        string populate = Slice(
            source,
            "private static bool PopulateContextMenuTarget(",
            "private static void PopulateInlineContextMenuTarget(");
        string build = Slice(
            source,
            "private static MarkdownTableClipboardSnapshot BuildTableClipboardSnapshot(",
            "private static string GetInlineClipboardText(");
        string copy = Slice(
            source,
            "private static async Task<bool> CopyTableTargetToClipboardAsync(",
            "private static MarkdownTableClipboardSnapshot BuildTableClipboardSnapshot(");
        string snapshot = Slice(
            source,
            "private sealed class MarkdownTableClipboardSnapshot",
            "internal readonly struct LinkTarget");
        string addItems = Slice(
            source,
            "private bool AddTargetContextMenuItems(",
            "private void AttachTargetCommand(");
        string execute = Slice(
            source,
            "private async Task<bool> ExecuteTargetCommandOrDefaultAsync(",
            "private static bool CopyTextTargetToClipboard(");

        Assert.DoesNotContain("MarkdownTableClipboardFormatter", find, StringComparison.Ordinal);
        Assert.DoesNotContain("MarkdownTableClipboardFormatter", populate, StringComparison.Ordinal);
        Assert.DoesNotContain("MarkdownTableClipboardFormatter", build, StringComparison.Ordinal);
        Assert.Contains("target.TableSnapshot = BuildTableClipboardSnapshot(table)", populate, StringComparison.Ordinal);
        Assert.Contains("Task.Run(() => snapshot.Format())", copy, StringComparison.Ordinal);
        Assert.Contains("MarkdownTableClipboardFormatter.Format(_rows, _headerRowCount)", snapshot, StringComparison.Ordinal);
        Assert.Contains("hasDefault: tableSnapshot.HasText", addItems, StringComparison.Ordinal);
        Assert.DoesNotContain("MarkdownTableClipboardFormatter", addItems, StringComparison.Ordinal);
        Assert.Contains("await CopyTableTargetToClipboardAsync(tableSnapshot)", execute, StringComparison.Ordinal);
        Assert.DoesNotContain("TableBox", snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("BlockBox", snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("LayoutSnapshot", snapshot, StringComparison.Ordinal);
    }

    [Fact]
    public void GestureMenusOpenOnlyAfterImmediateOrDeferredSnapshotLeaseEnds()
    {
        string control = ReadControl();
        string pointerInput = ReadPointerInput();
        string recognized = Slice(
            pointerInput,
            "private bool DispatchRecognizedGesture(",
            "private void DispatchPointerInput(");
        string retry = Slice(
            pointerInput,
            "private void OnPointerRetryTimer(",
            "private bool CompleteGestureAfterSnapshotInteraction(");
        string tapped = Slice(
            control,
            "private void ProcessTapped(",
            "private void ProcessDoubleTapped(");
        string rightTapped = Slice(
            control,
            "private void ProcessRightTapped(",
            "private bool ShowSelectionContextMenu(");
        string keyboard = Slice(
            control,
            "private void OnKeyDown(",
            "private bool HandleHorizontalArrowKey(");
        string keyboardPoint = Slice(
            control,
            "private Point GetKeyboardContextMenuPoint(",
            "internal static LinkTarget? FindLinkTargetInBlock(");
        string geometryRead = Slice(
            control,
            "private FocusGeometryReadStatus TryCaptureFocusableItemRect(",
            "private FocusGeometryReadStatus TryCaptureFocusableRectMap(");

        int directLeaseEnd = recognized.IndexOf("snapshot.EndInteraction()", StringComparison.Ordinal);
        int directCompletion = recognized.IndexOf("CompleteGestureAfterSnapshotInteraction(", StringComparison.Ordinal);
        Assert.True(directLeaseEnd >= 0 && directCompletion > directLeaseEnd);

        int deferredLeaseEnd = retry.IndexOf("snapshot.EndInteraction()", StringComparison.Ordinal);
        int deferredCompletion = retry.IndexOf("CompleteGestureAfterSnapshotInteraction(", StringComparison.Ordinal);
        Assert.True(deferredLeaseEnd >= 0 && deferredCompletion > deferredLeaseEnd);
        Assert.Contains("postInteractionInput = input", retry, StringComparison.Ordinal);
        Assert.Contains("continue;", retry, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowSelectionContextMenu(", tapped, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowSelectionContextMenu(", rightTapped, StringComparison.Ordinal);
        Assert.Contains("ShowContextMenuAfterInteraction = true", tapped, StringComparison.Ordinal);
        Assert.Contains("ShowContextMenuAfterInteraction = true", rightTapped, StringComparison.Ordinal);

        Assert.Contains("ShowSelectionContextMenu(GetKeyboardContextMenuPoint())", keyboard, StringComparison.Ordinal);
        Assert.Contains("GetFocusableItemRect(items[_focusedItemIndex])", keyboardPoint, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowSelectionContextMenu(", keyboardPoint, StringComparison.Ordinal);
        Assert.Contains("snapshot.TryBeginInteraction()", geometryRead, StringComparison.Ordinal);
        Assert.Contains("snapshot.EndInteraction()", geometryRead, StringComparison.Ordinal);
        Assert.Contains("finally", geometryRead, StringComparison.Ordinal);
    }

    [Fact]
    public void MenuCloseReleasesOwnerLeaseAndClickClonesBeforeCommandCanSuspend()
    {
        string source = ReadControl();
        string release = Slice(
            source,
            "private void ReleaseTargetContextMenuCommands(",
            "private bool AddTargetContextMenuItems(");
        string click = Slice(
            source,
            "private async void OnTargetContextMenuItemClick(",
            "private bool CanExecuteTargetCommandOrDefault(");
        string execute = Slice(
            source,
            "private async Task<bool> ExecuteTargetCommandOrDefaultAsync(",
            "private static bool CopyTextTargetToClipboard(");
        string copyImage = Slice(
            source,
            "private static async Task<bool> CopyImageTargetToClipboardAsync(",
            "private static bool CopyTableTargetToClipboard(");

        Assert.Contains("item.Tag = null", release, StringComparison.Ordinal);
        Assert.Contains("DisposeContextMenuTarget(command.Item2)", release, StringComparison.Ordinal);
        int clone = click.IndexOf("CloneForExecution()", StringComparison.Ordinal);
        int executeCommand = click.IndexOf("ExecuteTargetCommandOrDefaultAsync(", StringComparison.Ordinal);
        Assert.True(clone >= 0 && executeCommand > clone);
        Assert.Contains("CopyImageTargetToClipboardAsync(\n                    target.ImageUrl,", execute, StringComparison.Ordinal);
        Assert.Contains("CanvasBitmap? bitmap", copyImage, StringComparison.Ordinal);
        Assert.Contains("string? imageUrl", copyImage, StringComparison.Ordinal);
        Assert.Contains("package.SetText(imageUrl)", copyImage, StringComparison.Ordinal);
        Assert.DoesNotContain("Layout.Boxes.ImageBox", execute, StringComparison.Ordinal);
        Assert.DoesNotContain("Layout.Boxes.ImageBox", copyImage, StringComparison.Ordinal);
    }

    private static string ReadControl()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string path = Path.Combine(
                directory.FullName,
                "MarkdownRenderer",
                "MarkdownRenderer",
                "Controls",
                "MarkdownRendererControl.cs");
            if (File.Exists(path))
                return File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        }

        throw new FileNotFoundException("Could not locate the Markdown control source for context-menu checks.");
    }

    private static string ReadPointerInput()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string path = Path.Combine(
                directory.FullName,
                "MarkdownRenderer",
                "MarkdownRenderer",
                "Controls",
                "MarkdownRendererControl.PointerInput.cs");
            if (File.Exists(path))
                return File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        }

        throw new FileNotFoundException("Could not locate the Markdown pointer-input source for context-menu checks.");
    }

    private static string Slice(string source, string start, string end)
    {
        int first = source.IndexOf(start, StringComparison.Ordinal);
        int last = source.IndexOf(end, first + start.Length, StringComparison.Ordinal);
        Assert.True(first >= 0 && last > first, $"Could not find source slice '{start}'.");
        return source[first..last];
    }
}

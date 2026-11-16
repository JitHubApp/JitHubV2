using System;
using System.Collections.Generic;
using System.Threading;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.PrivatePlacement;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout.Internal.CodeBlocks;

/// <summary>
/// The private raster-placement state of one <see cref="CodeBlockBox"/>: the
/// placement revision that candidates fence on, the disposed flag that ends
/// every fence, the bounded preflight of the box's immutable shell inputs,
/// and chunk adoption while the box is an unmeasured clone shell.
/// </summary>
/// <remarks>
/// Owned by exactly one box and created with it. A clone shell's instance
/// starts at its source's revision and expects exactly the source's chunk
/// count; an ordinary box's instance is never a shell.
/// </remarks>
internal sealed class CodeBlockPrivatePlacement
{
    private readonly CodeBlockBox _box;
    private readonly List<InlineContainerBox> _chunks;
    private readonly bool _isCloneShell;
    private readonly int _expectedChunkCount;
    private long _revision;
    private int _disposed;
    private bool _cloneComplete;

    /// <summary>Creates the placement state of an ordinary, parser-built code block.</summary>
    /// <param name="box">The owning code block.</param>
    /// <param name="chunks">The owning block's chunk list.</param>
    internal CodeBlockPrivatePlacement(CodeBlockBox box, List<InlineContainerBox> chunks)
    {
        _box = box;
        _chunks = chunks;
    }

    /// <summary>Creates the placement state of an unmeasured private clone shell.</summary>
    /// <param name="shell">The owning clone shell.</param>
    /// <param name="chunks">The shell's chunk list, empty until chunks are adopted.</param>
    /// <param name="sourceRevision">The source box's revision when the shell was created.</param>
    /// <param name="expectedChunkCount">The number of chunks the shell must adopt before it can measure.</param>
    internal CodeBlockPrivatePlacement(
        CodeBlockBox shell,
        List<InlineContainerBox> chunks,
        long sourceRevision,
        int expectedChunkCount)
    {
        _box = shell;
        _chunks = chunks;
        _revision = sourceRevision;
        _isCloneShell = true;
        _expectedChunkCount = expectedChunkCount;
    }

    /// <summary>The current placement revision; every placement-affecting mutation advances it.</summary>
    internal long Revision => Interlocked.Read(ref _revision);

    /// <summary>
    /// The total number of syntax-highlight foreground spans across the box's
    /// chunks, saturating at <see cref="int.MaxValue"/>.
    /// </summary>
    internal int ForegroundSpanCount
    {
        get
        {
            int count = 0;
            foreach (InlineContainerBox chunk in _chunks)
            {
                int chunkCount = chunk.PrivateRasterPlacementForegroundSpanCount;
                if (chunkCount > int.MaxValue - count)
                    return int.MaxValue;
                count += chunkCount;
            }
            return count;
        }
    }

    /// <summary>Whether the box is undisposed and still at <paramref name="revision"/>.</summary>
    /// <param name="revision">The revision a candidate captured.</param>
    internal bool IsCurrent(long revision)
        => Volatile.Read(ref _disposed) == 0 && Revision == revision;

    /// <summary>Marks the box disposed; returns false when it already was.</summary>
    internal bool TryMarkDisposed() => Interlocked.Exchange(ref _disposed, 1) == 0;

    /// <summary>Throws when the box is a clone shell that has not adopted all of its chunks.</summary>
    internal void ThrowIfIncompleteCloneShell()
    {
        if (_isCloneShell && !_cloneComplete)
            throw new InvalidOperationException("A private code block cannot be measured before all chunks are adopted.");
    }

    /// <summary>Advances the placement revision so captured candidates stop matching.</summary>
    internal void Advance()
    {
        while (true)
        {
            long current = Interlocked.Read(ref _revision);
            if (current == long.MaxValue)
                throw new InvalidOperationException("The code-block placement revision is exhausted.");
            if (Interlocked.CompareExchange(ref _revision, current + 1, current) == current)
                return;
        }
    }

    /// <summary>
    /// Allocation-free eligibility and budget scan for the immutable shell
    /// inputs owned by the code block. Chunk run/image descriptors are scanned
    /// recursively by PrivateRasterPlacementTree and charged only there.
    /// </summary>
    /// <param name="targetContext">The generation context the clone would be built in.</param>
    /// <param name="allowUnmeasured">Whether a dirty box is eligible.</param>
    /// <param name="copiedLineCount">The number of line descriptors a clone copies.</param>
    /// <param name="styleKeyEntries">The number of style-key references a clone copies.</param>
    /// <returns>Whether the box can be cloned into <paramref name="targetContext"/>.</returns>
    internal bool TryGetCounts(
        MarkdownLayoutContext targetContext,
        bool allowUnmeasured,
        out int copiedLineCount,
        out long styleKeyEntries)
    {
        ArgumentNullException.ThrowIfNull(targetContext);
        CodeBlockBox box = _box;
        List<InlineContainerBox> chunks = _chunks;
        copiedLineCount = 0;
        styleKeyEntries = 0;
        targetContext.CancellationToken.ThrowIfCancellationRequested();
        if (!IsShellEligible(targetContext) ||
            (!allowUnmeasured && box.IsDirty) ||
            box.BlockIndex <= 0 ||
            box.LineCount is 0 or > PrivateRasterPlacementTree.MaximumNodes ||
            chunks.Count is 0 or > PrivateRasterPlacementTree.MaximumNodes ||
            !TryAccumulateStyleKeys(ref styleKeyEntries, box.StyleContextKeys) ||
            !TryAccumulateStyleKeys(ref styleKeyEntries, box.StyleAliasKeys))
            return false;

        int expectedOffset = 0;
        foreach (InlineContainerBox chunk in chunks)
        {
            targetContext.CancellationToken.ThrowIfCancellationRequested();
            if (chunk is null ||
                !ReferenceEquals(chunk.Context, box.Context) ||
                !IsChunkTextAt(chunk, expectedOffset) ||
                !chunk.TryGetPrivateRasterPlacementCounts(
                    targetContext,
                    Math.Max(1f, (float)chunk.Bounds.Width),
                    out _, out _, out _,
                    allowUnmeasuredTextOnly: allowUnmeasured,
                    allowIdleImagePlacements: false,
                    allowCodeForegroundSpans: true))
                return false;

            expectedOffset += chunk.CodeBlockTextLength;
        }

        if (expectedOffset != box.CodeText.Length ||
            !ContextMatchesTarget(targetContext))
            return false;

        copiedLineCount = box.LineCount;
        return true;
    }

    /// <summary>
    /// Validates a freshly cloned code-text chunk before the shell adopts it:
    /// it must belong to the shell's context and cover the next code-text
    /// range exactly, and the last chunk must end the payload.
    /// </summary>
    /// <param name="chunk">The cloned chunk.</param>
    internal void ValidateChunkAdoption(InlineContainerBox chunk)
    {
        List<InlineContainerBox> chunks = _chunks;
        int expectedOffset = chunks.Count == 0
            ? 0
            : checked(chunks[^1].CodeBlockTextOffset + chunks[^1].CodeBlockTextLength);
        if (chunks.Count >= PrivateRasterPlacementTree.MaximumNodes ||
            !_isCloneShell || _cloneComplete ||
            chunks.Count >= _expectedChunkCount ||
            !ReferenceEquals(chunk.Context, _box.Context) ||
            !IsChunkTextAt(chunk, expectedOffset))
            throw new InvalidOperationException("A code chunk does not match its private code block shell.");

        if (chunks.Count + 1 == _expectedChunkCount &&
            expectedOffset + chunk.CodeBlockTextLength != _box.CodeText.Length)
            throw new InvalidOperationException("The private code chunks do not cover their complete text payload.");
    }

    /// <summary>Records that the shell adopted a validated chunk.</summary>
    internal void RecordChunkAdopted()
        => _cloneComplete = _chunks.Count == _expectedChunkCount;

    /// <summary>Copies a style-key snapshot into a private array for a clone shell.</summary>
    /// <param name="source">The source box's style keys.</param>
    /// <returns>A new array with the same keys.</returns>
    internal static string[] CopyStyleKeys(IReadOnlyList<string> source)
    {
        var copy = new string[source.Count];
        for (int index = 0; index < copy.Length; index++)
            copy[index] = source[index];
        return copy;
    }

    private bool IsChunkTextAt(InlineContainerBox chunk, int expectedOffset)
    {
        string codeText = _box.CodeText;
        return chunk.ElementKey == MarkdownElementKeys.CodeBlock &&
            chunk.SafeHtmlInlineFlowScopeId is null &&
            chunk.CodeBlockTextOffset == expectedOffset &&
            chunk.CodeBlockTextLength >= 0 &&
            chunk.CodeBlockTextLength <= codeText.Length - expectedOffset &&
            chunk.CodeLanguage == _box.CodeLanguage &&
            chunk.BlockIndex > 0 &&
            chunk.Runs.Count == 1 &&
            chunk.Runs[0] is TextRun textRun &&
            textRun.Text.Length == chunk.CodeBlockTextLength &&
            codeText.AsSpan(expectedOffset, chunk.CodeBlockTextLength)
                .SequenceEqual(textRun.Text.AsSpan());
    }

    private bool IsShellEligible(MarkdownLayoutContext targetContext)
        => _box.SafeHtmlInlineFlowScopeId is null &&
           InlineContainerBox.ArePrivateRasterPlacementContextsCompatible(_box.Context, targetContext) &&
           _box.IsCopyButtonEnabled == targetContext.IsCodeBlockCopyEnabled;

    private bool ContextMatchesTarget(MarkdownLayoutContext targetContext)
    {
        MarkdownLayoutContext context = _box.Context;
        return IsShellEligible(targetContext) &&
           context.IsCodeBlockCopyEnabled == targetContext.IsCodeBlockCopyEnabled &&
           context.CodeBlockChromeMode == targetContext.CodeBlockChromeMode &&
           context.CodeBlockLineNumberMode == targetContext.CodeBlockLineNumberMode &&
           context.CodeBlockWrappingMode == targetContext.CodeBlockWrappingMode;
    }

    private static bool TryAccumulateStyleKeys(ref long total, IReadOnlyList<string>? keys)
    {
        if (keys is null || keys.Count < 0 ||
            total > InlineContainerBox.MaximumPrivateRasterPlacementStyleKeyCopyEntries - keys.Count)
            return false;
        total += keys.Count;
        return true;
    }
}

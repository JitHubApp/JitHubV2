using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Security.Cryptography;
using MarkdownRenderer.Accessibility.Internal.Peers;

namespace MarkdownRenderer.Accessibility.Internal.Semantics;

/// <summary>
/// Clamps each published semantic node range to the final text projection and
/// hashes node and span ranges, so automation clients can tell when ranges moved
/// even though the text did not.
/// </summary>
internal static class MarkdownSemanticRangeFingerprint
{
    private const int SemanticRangeFingerprintBatchBytes = 4 * 1024;

    internal static MarkdownAutomationTextFingerprint NormalizeNodeRangesAndComputeSemanticRangeFingerprint(
        MarkdownSemanticNode root,
        IReadOnlyList<MarkdownTextSpan> spans,
        int textLength)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> record = stackalloc byte[64];
        Span<byte> batch = stackalloc byte[SemanticRangeFingerprintBatchBytes];
        int batchLength = 0;
        foreach (MarkdownSemanticNode node in MarkdownSemanticDocument.EnumerateDepthFirst(root))
        {
            // The builder trims synthetic terminal separators after creating semantic
            // nodes. Keep each published range inside the final projection;
            // this is folded into the existing fingerprint traversal so it
            // does not add a second document-tree walk.
            node.TextStart = Math.Clamp(node.TextStart, 0, textLength);
            node.TextEnd = Math.Clamp(node.TextEnd, node.TextStart, textLength);
            AppendNodeRangeProjection(node, record);
            AppendFingerprintRecord(hash, batch, ref batchLength, record);
        }
        for (int index = 0; index < spans.Count; index++)
        {
            MarkdownTextSpan span = spans[index];
            record.Clear();
            record[0] = 0x53;
            BinaryPrimitives.WriteInt32LittleEndian(record[4..], span.TextStart);
            BinaryPrimitives.WriteInt32LittleEndian(record[8..], span.TextEnd);
            BinaryPrimitives.WriteInt32LittleEndian(record[12..], span.InlineBox?.BlockIndex ?? -1);
            BinaryPrimitives.WriteInt32LittleEndian(record[16..], span.InlineRun?.InlineIndex ?? -1);
            BinaryPrimitives.WriteInt32LittleEndian(record[20..], span.ImageBox?.BlockIndex ?? -1);
            BinaryPrimitives.WriteInt32LittleEndian(record[24..], span.EmbedBox?.BlockIndex ?? -1);
            BinaryPrimitives.WriteInt32LittleEndian(record[28..], span.HostedElementBox?.BlockIndex ?? -1);
            BinaryPrimitives.WriteInt32LittleEndian(record[32..], span.VectorSceneBox?.BlockIndex ?? -1);
            BinaryPrimitives.WriteInt32LittleEndian(record[36..], span.VectorSemanticIndex);
            BinaryPrimitives.WriteInt32LittleEndian(record[40..], span.InlineRun?.SourceSpan.Start ?? -1);
            BinaryPrimitives.WriteInt32LittleEndian(record[44..], span.InlineRun?.SourceSpan.Length ?? -1);
            int kindFlags =
                (span.InlineBox is null ? 0 : 1) |
                (span.InlineRun is null ? 0 : 2) |
                (span.ImageBox is null ? 0 : 4) |
                (span.EmbedBox is null ? 0 : 8) |
                (span.HostedElementBox is null ? 0 : 16) |
                (span.VectorSceneBox is null ? 0 : 32);
            BinaryPrimitives.WriteInt32LittleEndian(record[48..], kindFlags);
            AppendFingerprintRecord(hash, batch, ref batchLength, record[..52]);
        }

        if (batchLength > 0)
            hash.AppendData(batch[..batchLength]);

        Span<byte> digest = stackalloc byte[32];
        hash.GetHashAndReset(digest);
        return MarkdownAutomationTextFingerprint.FromSha256(textLength, digest);
    }

    private static void AppendFingerprintRecord(
        IncrementalHash hash,
        Span<byte> batch,
        ref int batchLength,
        ReadOnlySpan<byte> record)
    {
        if (batch.Length - batchLength < record.Length)
        {
            hash.AppendData(batch[..batchLength]);
            batchLength = 0;
        }

        record.CopyTo(batch[batchLength..]);
        batchLength += record.Length;
    }

    private static void AppendNodeRangeProjection(
        MarkdownSemanticNode node,
        Span<byte> record)
    {
        record.Clear();
        record[0] = 0x4E;
        BinaryPrimitives.WriteInt32LittleEndian(record[4..], (int)node.Role);
        BinaryPrimitives.WriteInt32LittleEndian(record[8..], node.TextStart);
        BinaryPrimitives.WriteInt32LittleEndian(record[12..], node.TextEnd);
        BinaryPrimitives.WriteInt32LittleEndian(record[16..], node.Box?.BlockIndex ?? -1);
        BinaryPrimitives.WriteInt32LittleEndian(record[20..], node.InlineBox?.BlockIndex ?? -1);
        BinaryPrimitives.WriteInt32LittleEndian(record[24..], node.ImageBox?.BlockIndex ?? -1);
        BinaryPrimitives.WriteInt32LittleEndian(record[28..], node.EmbedBox?.BlockIndex ?? -1);
        BinaryPrimitives.WriteInt32LittleEndian(record[32..], node.HostedElementBox?.BlockIndex ?? -1);
        BinaryPrimitives.WriteInt32LittleEndian(record[36..], node.VectorSceneBox?.BlockIndex ?? -1);
        BinaryPrimitives.WriteInt32LittleEndian(record[40..], node.InlineRun?.InlineIndex ?? -1);
        BinaryPrimitives.WriteInt32LittleEndian(record[44..], node.VectorSemanticIndex);
        BinaryPrimitives.WriteInt32LittleEndian(record[48..], node.HeadingLevel);
        BinaryPrimitives.WriteInt32LittleEndian(record[52..], node.Row);
        BinaryPrimitives.WriteInt32LittleEndian(record[56..], node.Column);
        BinaryPrimitives.WriteInt32LittleEndian(record[60..], (int)node.AccessibilityRole);
    }
}

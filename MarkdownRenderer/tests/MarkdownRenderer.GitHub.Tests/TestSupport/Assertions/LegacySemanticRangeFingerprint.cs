using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Security.Cryptography;
using MarkdownRenderer.Accessibility.Internal.Peers;
using MarkdownRenderer.Accessibility.Internal.Semantics;

namespace MarkdownRenderer.GitHub.Tests.TestSupport.Assertions;

/// <summary>
/// Independent oracle for <see cref="MarkdownSemanticDocument.SemanticRangeFingerprint"/>:
/// the pre-batching implementation that hashed one 64-byte node record or one
/// 52-byte span record per <c>AppendData</c> call, in depth-first node order
/// followed by span order.
/// </summary>
internal static class LegacySemanticRangeFingerprint
{
    public static MarkdownAutomationTextFingerprint Compute(
        MarkdownSemanticNode root,
        IReadOnlyList<MarkdownTextSpan> spans,
        int textLength)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> record = stackalloc byte[64];
        foreach (MarkdownSemanticNode node in MarkdownSemanticDocument.EnumerateDepthFirst(root))
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
            hash.AppendData(record);
        }

        foreach (MarkdownTextSpan span in spans)
        {
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
            hash.AppendData(record[..52]);
        }

        Span<byte> digest = stackalloc byte[32];
        hash.GetHashAndReset(digest);
        return MarkdownAutomationTextFingerprint.FromSha256(textLength, digest);
    }
}

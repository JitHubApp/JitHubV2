using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using MarkdownRenderer.Extensions;

namespace MarkdownRenderer.Performance;

/// <summary>
/// Stable, scene-free identity for the complete projection metadata of one
/// declarative vector-scene leaf. This value intentionally retains no content
/// or scene object.
/// </summary>
internal readonly struct MarkdownSceneProjectionSignature : IEquatable<MarkdownSceneProjectionSignature>
{
    private const int StringChunkCharacters = 2048;

    private readonly ulong _digest0;
    private readonly ulong _digest1;
    private readonly ulong _digest2;
    private readonly ulong _digest3;

    private MarkdownSceneProjectionSignature(ulong digest0, ulong digest1, ulong digest2, ulong digest3)
    {
        _digest0 = digest0;
        _digest1 = digest1;
        _digest2 = digest2;
        _digest3 = digest3;
    }

    internal static MarkdownSceneProjectionSignature Create(
        MarkdownContent content,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(content);
        if (content.Kind != MarkdownContentKind.VectorScene ||
            content.VectorScene is not { } scene ||
            content.Children.Count != 0)
        {
            throw new ArgumentException(
                "A scene projection signature requires one vector-scene leaf.",
                nameof(content));
        }

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("MarkdownRenderer.SceneProjection.v1"u8);
        WriteContent(hash, content, cancellationToken);
        WriteScene(hash, scene, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        Span<byte> digest = stackalloc byte[32];
        if (!hash.TryGetHashAndReset(digest, out int written) || written != digest.Length)
            throw new CryptographicException("SHA-256 returned an unexpected digest length.");

        return new MarkdownSceneProjectionSignature(
            BinaryPrimitives.ReadUInt64LittleEndian(digest),
            BinaryPrimitives.ReadUInt64LittleEndian(digest[8..]),
            BinaryPrimitives.ReadUInt64LittleEndian(digest[16..]),
            BinaryPrimitives.ReadUInt64LittleEndian(digest[24..]));
    }

    public bool Equals(MarkdownSceneProjectionSignature other) =>
        _digest0 == other._digest0 &&
        _digest1 == other._digest1 &&
        _digest2 == other._digest2 &&
        _digest3 == other._digest3;

    public override bool Equals(object? obj) =>
        obj is MarkdownSceneProjectionSignature other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_digest0, _digest1, _digest2, _digest3);

    public static bool operator ==(
        MarkdownSceneProjectionSignature left,
        MarkdownSceneProjectionSignature right) => left.Equals(right);

    public static bool operator !=(
        MarkdownSceneProjectionSignature left,
        MarkdownSceneProjectionSignature right) => !left.Equals(right);

    private static void WriteContent(
        IncrementalHash hash,
        MarkdownContent content,
        CancellationToken cancellationToken)
    {
        WriteInt32(hash, (int)content.Kind);
        WriteString(hash, content.StyleRole.Name, cancellationToken);
        WriteInt32(hash, content.SourceSpan.Start);
        WriteInt32(hash, content.SourceSpan.Length);
        WriteInt32(hash, (int)content.AccessibilityRole);
        WriteString(hash, content.Text, cancellationToken);
        WriteString(hash, content.Destination, cancellationToken);
        WriteString(hash, content.Language, cancellationToken);
        WriteString(hash, content.FactoryKey, cancellationToken);
        WriteString(hash, content.CustomKind, cancellationToken);
        WriteString(hash, content.AccessibilityName, cancellationToken);
        WriteString(hash, content.AccessibilityDescription, cancellationToken);
        WriteString(hash, content.SemanticText, cancellationToken);
        WriteInt32(hash, content.Children.Count);
        WriteAttributes(hash, content.Attributes, cancellationToken);
    }

    private static void WriteScene(
        IncrementalHash hash,
        MarkdownVectorScene scene,
        CancellationToken cancellationToken)
    {
        WriteSingle(hash, scene.Width);
        WriteSingle(hash, scene.Height);
        WriteSingle(hash, scene.Baseline);
        WriteSingle(hash, scene.ReferenceFontSize);
        WriteRectangle(hash, scene.Viewport);

        WriteInt32(hash, scene.Commands.Count);
        for (int index = 0; index < scene.Commands.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteCommand(hash, scene.Commands[index], cancellationToken);
        }

        WriteInt32(hash, scene.Semantics.Count);
        for (int index = 0; index < scene.Semantics.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteSemanticItem(hash, scene.Semantics[index], cancellationToken);
        }

        WriteInt32(hash, scene.Links.Count);
        for (int index = 0; index < scene.Links.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteLinkAction(hash, scene.Links[index], cancellationToken);
        }
    }

    private static void WriteCommand(
        IncrementalHash hash,
        MarkdownVectorCommand command,
        CancellationToken cancellationToken)
    {
        WriteInt32(hash, (int)command.Kind);
        WriteInt32(hash, command.Path.Count);
        for (int index = 0; index < command.Path.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MarkdownVectorPathOperation operation = command.Path[index];
            WriteInt32(hash, (int)operation.Kind);
            WritePoint(hash, operation.Point1);
            WritePoint(hash, operation.Point2);
            WritePoint(hash, operation.Point3);
        }

        WriteRectangle(hash, command.Rectangle);
        WritePoint(hash, command.Start);
        WritePoint(hash, command.End);
        WriteSingle(hash, command.StrokeWidth);
        WriteNullableUInt32(hash, command.ColorArgb);
        WriteBoolean(hash, command.Style is not null);
        if (command.Style is { } style)
            WritePaintStyle(hash, style, cancellationToken);
        WriteString(hash, command.Text, cancellationToken);
        WriteBoolean(hash, command.TextStyle is not null);
        if (command.TextStyle is { } textStyle)
            WriteTextStyle(hash, textStyle, cancellationToken);
        WriteTransform(hash, command.Transform);
        WriteInt32(hash, command.SemanticIndex);
        WriteBoolean(hash, command.IsClosed);
    }

    private static void WritePaintStyle(
        IncrementalHash hash,
        MarkdownVectorPaintStyle style,
        CancellationToken cancellationToken)
    {
        WriteNullableUInt32(hash, style.FillArgb);
        WriteNullableUInt32(hash, style.StrokeArgb);
        WriteInt32(hash, (int)style.FillRole);
        WriteInt32(hash, (int)style.StrokeRole);
        WriteNullablePaintRole(hash, style.HighContrastFillRole);
        WriteNullablePaintRole(hash, style.HighContrastStrokeRole);
        WriteSingle(hash, style.StrokeWidth);
        WriteSingle(hash, style.Opacity);
        WriteInt32(hash, (int)style.FillRule);
        WriteInt32(hash, (int)style.LineCap);
        WriteInt32(hash, (int)style.LineJoin);
        WriteBoolean(hash, style.NonScalingStroke);
        WriteInt32(hash, style.DashArray.Count);
        for (int index = 0; index < style.DashArray.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteSingle(hash, style.DashArray[index]);
        }
        WriteSingle(hash, style.DashOffset);
    }

    private static void WriteTextStyle(
        IncrementalHash hash,
        MarkdownVectorTextStyle style,
        CancellationToken cancellationToken)
    {
        WriteString(hash, style.FontFamily, cancellationToken);
        WriteInt32(hash, (int)style.FontRole);
        WriteSingle(hash, style.FontSize);
        WriteUInt16(hash, style.FontWeight);
        WriteBoolean(hash, style.Italic);
        WriteBoolean(hash, style.RightToLeft);
        WriteInt32(hash, (int)style.Alignment);
    }

    private static void WriteSemanticItem(
        IncrementalHash hash,
        MarkdownVectorSemanticItem item,
        CancellationToken cancellationToken)
    {
        WriteUInt32(hash, item.SourceId);
        WriteInt32(hash, (int)item.Role);
        WriteString(hash, item.Name, cancellationToken);
        WriteString(hash, item.Description, cancellationToken);
        WriteInt32(hash, item.ParentIndex);
        WriteInt32(hash, item.SourceSpan.Start);
        WriteInt32(hash, item.SourceSpan.Length);
        WriteRectangle(hash, item.Bounds);
        WriteInt32(hash, (int)item.Flags);
    }

    private static void WriteLinkAction(
        IncrementalHash hash,
        MarkdownVectorLinkAction link,
        CancellationToken cancellationToken)
    {
        WriteInt32(hash, link.SemanticIndex);
        WriteString(hash, link.Target, cancellationToken);
        WriteString(hash, link.Action, cancellationToken);
        WriteBoolean(hash, link.External);
    }

    private static void WriteAttributes(
        IncrementalHash hash,
        IReadOnlyDictionary<string, string> attributes,
        CancellationToken cancellationToken)
    {
        int count = attributes.Count;
        WriteInt32(hash, count);
        if (count == 0)
            return;

        // Attribute iteration order is supplied by extensions; use one local
        // key/value buffer and an ordinal in-place heap sort for canonical bytes.
        var entries = new KeyValuePair<string, string>[count];
        int entryCount = 0;
        foreach (KeyValuePair<string, string> entry in attributes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entryCount == entries.Length)
                throw new InvalidOperationException("Content attributes changed during signature computation.");
            entries[entryCount++] = entry;
        }

        if (entryCount != entries.Length)
            throw new InvalidOperationException("Content attributes changed during signature computation.");

        SortAttributes(entries, cancellationToken);
        for (int index = 0; index < entries.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteString(hash, entries[index].Key, cancellationToken);
            WriteString(hash, entries[index].Value, cancellationToken);
        }
    }

    private static void SortAttributes(
        KeyValuePair<string, string>[] entries,
        CancellationToken cancellationToken)
    {
        int count = entries.Length;
        for (int root = (count / 2) - 1; root >= 0; root--)
            SiftDown(entries, root, count, cancellationToken);

        for (int end = count - 1; end > 0; end--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (entries[0], entries[end]) = (entries[end], entries[0]);
            SiftDown(entries, 0, end, cancellationToken);
        }
    }

    private static void SiftDown(
        KeyValuePair<string, string>[] entries,
        int root,
        int heapLength,
        CancellationToken cancellationToken)
    {
        while (root < heapLength / 2)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int child = (root * 2) + 1;
            if (child + 1 < heapLength &&
                CompareOrdinalWithCancellation(
                    entries[child].Key,
                    entries[child + 1].Key,
                    cancellationToken) < 0)
            {
                child++;
            }

            if (CompareOrdinalWithCancellation(
                entries[root].Key,
                entries[child].Key,
                cancellationToken) >= 0)
            {
                return;
            }

            (entries[root], entries[child]) = (entries[child], entries[root]);
            root = child;
        }
    }

    private static int CompareOrdinalWithCancellation(
        string left,
        string right,
        CancellationToken cancellationToken)
    {
        int commonLength = Math.Min(left.Length, right.Length);
        for (int offset = 0; offset < commonLength;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(StringChunkCharacters, commonLength - offset);
            int end = offset + count;
            for (int index = offset; index < end; index++)
            {
                int difference = left[index] - right[index];
                if (difference != 0)
                    return difference;
            }
            offset = end;
        }

        return left.Length.CompareTo(right.Length);
    }

    private static void WritePoint(IncrementalHash hash, MarkdownVectorPoint point)
    {
        WriteSingle(hash, point.X);
        WriteSingle(hash, point.Y);
    }

    private static void WriteRectangle(IncrementalHash hash, MarkdownVectorRectangle rectangle)
    {
        WriteSingle(hash, rectangle.X);
        WriteSingle(hash, rectangle.Y);
        WriteSingle(hash, rectangle.Width);
        WriteSingle(hash, rectangle.Height);
    }

    private static void WriteTransform(IncrementalHash hash, MarkdownVectorTransform transform)
    {
        WriteSingle(hash, transform.M11);
        WriteSingle(hash, transform.M12);
        WriteSingle(hash, transform.M21);
        WriteSingle(hash, transform.M22);
        WriteSingle(hash, transform.M31);
        WriteSingle(hash, transform.M32);
    }

    private static void WriteString(
        IncrementalHash hash,
        string? value,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Span<byte> header = stackalloc byte[5];
        if (value is null)
        {
            header[0] = 0;
            hash.AppendData(header[..1]);
            return;
        }

        header[0] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(header[1..], value.Length);
        hash.AppendData(header);

        ReadOnlySpan<char> characters = value.AsSpan();
        for (int offset = 0; offset < characters.Length;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(StringChunkCharacters, characters.Length - offset);
            // The renderer targets Windows, whose UTF-16 char storage is little-endian.
            hash.AppendData(MemoryMarshal.AsBytes(characters.Slice(offset, count)));
            offset += count;
        }
    }

    private static void WriteNullableUInt32(IncrementalHash hash, uint? value)
    {
        WriteBoolean(hash, value.HasValue);
        if (value is { } number)
            WriteUInt32(hash, number);
    }

    private static void WriteNullablePaintRole(
        IncrementalHash hash,
        MarkdownVectorPaintRole? value)
    {
        WriteBoolean(hash, value.HasValue);
        if (value is { } role)
            WriteInt32(hash, (int)role);
    }

    private static void WriteBoolean(IncrementalHash hash, bool value)
    {
        Span<byte> bytes = stackalloc byte[1];
        bytes[0] = value ? (byte)1 : (byte)0;
        hash.AppendData(bytes);
    }

    private static void WriteInt32(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void WriteUInt16(IncrementalHash hash, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void WriteUInt32(IncrementalHash hash, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void WriteSingle(IncrementalHash hash, float value) =>
        WriteInt32(hash, BitConverter.SingleToInt32Bits(value));
}

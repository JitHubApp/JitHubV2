using System.Text;
using static MarkdownRenderer.Mermaid.MmirFormat;
using static MarkdownRenderer.Mermaid.MmirPrimitives;

namespace MarkdownRenderer.Mermaid;

/// <summary>
/// Validates and materializes the payload of each MMIR section. Every parser treats the buffer as
/// untrusted and reports the first format error through its failure result.
/// </summary>
internal static class MermaidSceneSectionParser
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static MermaidSceneDecodeResult? ValidateSectionShapes(
        Section[] sections,
        MermaidRenderBudgets budgets,
        CancellationToken cancellationToken)
    {
        long semanticLimit = checked((long)budgets.MaxNodes + budgets.MaxEdges + 1);
        long commandLimit = checked(((long)budgets.MaxNodes + budgets.MaxEdges) * 16 + 1_024);
        long stringLimit = checked(((long)budgets.MaxNodes + budgets.MaxEdges) * 8 + 4_096);
        long mappingLimit = checked(((long)budgets.MaxNodes + budgets.MaxEdges) * 4 + 1_024);

        for (int sectionIndex = 0; sectionIndex < sections.Length; sectionIndex++)
        {
            ThrowIfCancellationRequestedPeriodically(cancellationToken, sectionIndex);
            Section section = sections[sectionIndex];
            (int expectedStride, long maximumCount) = section.Kind switch
            {
                SectionKind.Viewport => (16, 1),
                SectionKind.Strings => (0, stringLimit),
                SectionKind.Styles => (32, semanticLimit + 1_024),
                SectionKind.Geometry => (4, budgets.MaxSceneBytes / 4L),
                SectionKind.DrawCommands => (32, commandLimit),
                SectionKind.Semantics => (32, semanticLimit),
                SectionKind.Links => (16, semanticLimit),
                SectionKind.Diagnostics => (24, 4_096),
                SectionKind.SourceMappings => (20, mappingLimit),
                _ => (-1, budgets.MaxSceneBytes),
            };

            if (expectedStride >= 0 && section.Stride != expectedStride)
            {
                return Failure(MermaidSceneDecodeStatus.InvalidSection, section.Offset, $"{section.Kind} has an invalid stride.");
            }

            if (section.Count > maximumCount)
            {
                return Failure(MermaidSceneDecodeStatus.BudgetExceeded, section.Offset, $"{section.Kind} exceeds its element budget.");
            }

            if (section.Kind == SectionKind.Viewport && (section.Count != 1 || section.Length != 16))
            {
                return Failure(MermaidSceneDecodeStatus.InvalidSection, section.Offset, "The viewport section must contain one record.");
            }
        }

        return null;
    }

    internal static bool TryParseViewport(
        ReadOnlySpan<byte> buffer,
        Section section,
        out MermaidViewport viewport,
        out MermaidSceneDecodeResult? failure)
    {
        float x = ReadSingle(buffer, section.Offset);
        float y = ReadSingle(buffer, section.Offset + 4);
        float width = ReadSingle(buffer, section.Offset + 8);
        float height = ReadSingle(buffer, section.Offset + 12);
        if (!AreFinite(x, y, width, height) || width <= 0 || height <= 0)
        {
            viewport = default;
            failure = Failure(MermaidSceneDecodeStatus.NonFiniteNumber, section.Offset, "The scene viewport is not finite and positive.");
            return false;
        }

        viewport = new MermaidViewport(x, y, width, height);
        failure = null;
        return true;
    }

    internal static bool TryParseStrings(
        ReadOnlySpan<byte> buffer,
        Section section,
        MermaidRenderBudgets budgets,
        CancellationToken cancellationToken,
        out string[] strings,
        out MermaidSceneDecodeResult? failure)
    {
        strings = new string[section.Count];
        int cursor = section.Offset;
        int end = checked(section.Offset + section.Length);
        for (int i = 0; i < strings.Length; i++)
        {
            ThrowIfCancellationRequestedPeriodically(cancellationToken, i);
            if (end - cursor < 4 || !TryUIntToInt(ReadUInt32(buffer, cursor), out int byteLength) ||
                byteLength > budgets.MaxLabelBytes || !IsRangeInBounds(cursor + 4, byteLength, end))
            {
                failure = Failure(MermaidSceneDecodeStatus.InvalidSection, cursor, "A string record is invalid or exceeds MaxLabelBytes.");
                return false;
            }

            strings[i] = StrictUtf8.GetString(buffer.Slice(cursor + 4, byteLength));
            int unalignedCursor = checked(cursor + 4 + byteLength);
            cursor = checked((unalignedCursor + 3) & ~3);
            if (cursor > end || !IsZero(buffer.Slice(unalignedCursor, cursor - unalignedCursor)))
            {
                failure = Failure(MermaidSceneDecodeStatus.InvalidSection, end, "String padding is nonzero or extends past its section.");
                return false;
            }
        }

        if (cursor != end)
        {
            failure = Failure(MermaidSceneDecodeStatus.InvalidSection, cursor, "The string section contains trailing data.");
            return false;
        }

        failure = null;
        return true;
    }

    internal static float[] ParseGeometry(
        ReadOnlySpan<byte> buffer,
        Section section,
        CancellationToken cancellationToken,
        out MermaidSceneDecodeResult? failure)
    {
        var geometry = new float[section.Count];
        for (int i = 0; i < geometry.Length; i++)
        {
            ThrowIfCancellationRequestedPeriodically(cancellationToken, i);
            float value = ReadSingle(buffer, checked(section.Offset + (i * 4)));
            if (!float.IsFinite(value))
            {
                failure = Failure(MermaidSceneDecodeStatus.NonFiniteNumber, section.Offset + (i * 4), "Geometry contains a non-finite coordinate.");
                return [];
            }

            geometry[i] = value;
        }

        failure = null;
        return geometry;
    }

    internal static MermaidSceneStyle[] ParseStyles(
        ReadOnlySpan<byte> buffer,
        Section section,
        int geometryCount,
        CancellationToken cancellationToken,
        out MermaidSceneDecodeResult? failure)
    {
        var styles = new MermaidSceneStyle[section.Count];
        const uint allowedFlags = (uint)(MermaidStyleFlags.NonScalingStroke | MermaidStyleFlags.RoundLineCap | MermaidStyleFlags.RoundLineJoin);
        for (int i = 0; i < styles.Length; i++)
        {
            ThrowIfCancellationRequestedPeriodically(cancellationToken, i);
            int offset = checked(section.Offset + (i * section.Stride));
            float strokeWidth = ReadSingle(buffer, offset + 8);
            float opacity = ReadSingle(buffer, offset + 12);
            uint flags = ReadUInt32(buffer, offset + 16);
            uint dashStart = ReadUInt32(buffer, offset + 20);
            uint dashCount = ReadUInt32(buffer, offset + 24);
            if (!float.IsFinite(strokeWidth) || !float.IsFinite(opacity) || strokeWidth < 0 || opacity is < 0 or > 1 ||
                (flags & ~allowedFlags) != 0 || ReadUInt32(buffer, offset + 28) != 0 ||
                !IsUnsignedRangeInBounds(dashStart, dashCount, geometryCount))
            {
                failure = Failure(MermaidSceneDecodeStatus.InvalidSection, offset, "A scene style is invalid.");
                return [];
            }

            styles[i] = new MermaidSceneStyle(
                MermaidRgba32.FromPacked(ReadUInt32(buffer, offset)),
                MermaidRgba32.FromPacked(ReadUInt32(buffer, offset + 4)),
                strokeWidth,
                opacity,
                (MermaidStyleFlags)flags,
                dashStart,
                dashCount);
        }

        failure = null;
        return styles;
    }

    internal static MermaidDrawCommand[] ParseCommands(
        ReadOnlySpan<byte> buffer,
        Section section,
        ushort minor,
        int styleCount,
        int semanticCount,
        int stringCount,
        float[] geometry,
        CancellationToken cancellationToken,
        out MermaidSceneDecodeResult? failure)
    {
        var commands = new MermaidDrawCommand[section.Count];
        const ushort allowedFlags = (ushort)(MermaidDrawFlags.EvenOddFill | MermaidDrawFlags.ClosedGeometry | MermaidDrawFlags.Decorative);
        for (int i = 0; i < commands.Length; i++)
        {
            ThrowIfCancellationRequestedPeriodically(cancellationToken, i);
            int offset = checked(section.Offset + (i * section.Stride));
            ushort rawOpcode = ReadUInt16(buffer, offset);
            ushort rawFlags = ReadUInt16(buffer, offset + 2);
            uint style = ReadUInt32(buffer, offset + 4);
            uint semantic = ReadUInt32(buffer, offset + 8);
            uint text = ReadUInt32(buffer, offset + 12);
            uint geometryStart = ReadUInt32(buffer, offset + 16);
            uint geometryLength = ReadUInt32(buffer, offset + 20);
            uint fontFamily = ReadUInt32(buffer, offset + 24);
            uint textFlags = ReadUInt32(buffer, offset + 28);
            const uint allowedTextFlags = (uint)(MermaidTextFlags.Italic | MermaidTextFlags.RightToLeft |
                MermaidTextFlags.AnchorMiddle | MermaidTextFlags.AnchorEnd);

            if (!Enum.IsDefined((MermaidDrawOpcode)rawOpcode) || (rawFlags & ~allowedFlags) != 0 ||
                !IsOptionalIndex(style, styleCount) || !IsOptionalIndex(semantic, semanticCount) ||
                !IsOptionalIndex(text, stringCount) || !IsUnsignedRangeInBounds(geometryStart, geometryLength, geometry.Length))
            {
                failure = Failure(MermaidSceneDecodeStatus.InvalidReference, offset, "A draw command contains an invalid opcode, flag, or reference.");
                return [];
            }

            MermaidDrawOpcode opcode = (MermaidDrawOpcode)rawOpcode;
            bool currentText = minor >= 1 && opcode == MermaidDrawOpcode.Text;
            bool currentNonText = minor >= 1 && opcode != MermaidDrawOpcode.Text;
            if (!HasValidGeometryArity(opcode, geometryLength, minor) ||
                (opcode == MermaidDrawOpcode.Text && text == NoIndex) ||
                (currentText && (!IsRequiredIndex(fontFamily, stringCount) || (textFlags & ~allowedTextFlags) != 0 ||
                    (textFlags & (uint)(MermaidTextFlags.AnchorMiddle | MermaidTextFlags.AnchorEnd)) ==
                    (uint)(MermaidTextFlags.AnchorMiddle | MermaidTextFlags.AnchorEnd) ||
                    geometry[checked((int)geometryStart + 2)] <= 0 ||
                    geometry[checked((int)geometryStart + 3)] is < 1 or > 999)) ||
                (currentNonText && (fontFamily != NoIndex || textFlags != 0)) ||
                (minor == 0 && (fontFamily != 0 || textFlags != 0)))
            {
                failure = Failure(MermaidSceneDecodeStatus.InvalidSection, offset, "A draw command has invalid geometry or text data.");
                return [];
            }

            commands[i] = new MermaidDrawCommand(
                opcode,
                (MermaidDrawFlags)rawFlags,
                OptionalIndex(style),
                OptionalIndex(semantic),
                OptionalIndex(text),
                checked((int)geometryStart),
                checked((int)geometryLength),
                currentText ? checked((int)fontFamily) : -1,
                currentText ? (MermaidTextFlags)textFlags : MermaidTextFlags.None);
        }

        failure = null;
        return commands;
    }

    internal static MermaidSemanticItem[] ParseSemantics(
        ReadOnlySpan<byte> buffer,
        Section section,
        int stringCount,
        int mappingCount,
        MermaidRenderBudgets budgets,
        CancellationToken cancellationToken,
        out MermaidSceneDecodeResult? failure)
    {
        var semantics = new MermaidSemanticItem[section.Count];
        int nodeCount = 0;
        int edgeCount = 0;
        const uint allowedFlags = (uint)(MermaidSemanticFlags.Focusable | MermaidSemanticFlags.Selectable | MermaidSemanticFlags.Linked | MermaidSemanticFlags.Decorative);

        for (int i = 0; i < semantics.Length; i++)
        {
            ThrowIfCancellationRequestedPeriodically(cancellationToken, i);
            int offset = checked(section.Offset + (i * section.Stride));
            uint rawRole = ReadUInt32(buffer, offset + 4);
            uint name = ReadUInt32(buffer, offset + 8);
            uint description = ReadUInt32(buffer, offset + 12);
            uint parent = ReadUInt32(buffer, offset + 16);
            uint mapping = ReadUInt32(buffer, offset + 20);
            uint flags = ReadUInt32(buffer, offset + 24);
            if (!Enum.IsDefined((MermaidSemanticRole)rawRole) || !IsOptionalIndex(name, stringCount) ||
                !IsOptionalIndex(description, stringCount) || !IsOptionalIndex(parent, semantics.Length) ||
                !IsOptionalIndex(mapping, mappingCount) || (flags & ~allowedFlags) != 0 ||
                ReadUInt32(buffer, offset + 28) != 0 || parent == (uint)i)
            {
                failure = Failure(MermaidSceneDecodeStatus.InvalidReference, offset, "A semantic record contains an invalid role, flag, or reference.");
                return [];
            }

            MermaidSemanticRole role = (MermaidSemanticRole)rawRole;
            nodeCount += role == MermaidSemanticRole.Node ? 1 : 0;
            edgeCount += role == MermaidSemanticRole.Edge ? 1 : 0;
            if (nodeCount > budgets.MaxNodes || edgeCount > budgets.MaxEdges)
            {
                failure = Failure(MermaidSceneDecodeStatus.BudgetExceeded, offset, "The scene exceeds its node or edge budget.");
                return [];
            }

            semantics[i] = new MermaidSemanticItem(
                ReadUInt32(buffer, offset),
                role,
                OptionalIndex(name),
                OptionalIndex(description),
                OptionalIndex(parent),
                OptionalIndex(mapping),
                (MermaidSemanticFlags)flags);
        }

        failure = null;
        return semantics;
    }

    internal static MermaidSceneLink[] ParseLinks(
        ReadOnlySpan<byte> buffer,
        Section section,
        int semanticCount,
        int stringCount,
        CancellationToken cancellationToken,
        out MermaidSceneDecodeResult? failure)
    {
        var links = new MermaidSceneLink[section.Count];
        const uint allowedFlags = (uint)(MermaidLinkFlags.External | MermaidLinkFlags.Action);
        for (int i = 0; i < links.Length; i++)
        {
            ThrowIfCancellationRequestedPeriodically(cancellationToken, i);
            int offset = checked(section.Offset + (i * section.Stride));
            uint semantic = ReadUInt32(buffer, offset);
            uint target = ReadUInt32(buffer, offset + 4);
            uint action = ReadUInt32(buffer, offset + 8);
            uint flags = ReadUInt32(buffer, offset + 12);
            if (!IsRequiredIndex(semantic, semanticCount) || !IsOptionalIndex(target, stringCount) ||
                !IsOptionalIndex(action, stringCount) || (target == NoIndex && action == NoIndex) ||
                (flags & ~allowedFlags) != 0)
            {
                failure = Failure(MermaidSceneDecodeStatus.InvalidReference, offset, "A link record contains an invalid reference or flag.");
                return [];
            }

            links[i] = new MermaidSceneLink(checked((int)semantic), OptionalIndex(target), OptionalIndex(action), (MermaidLinkFlags)flags);
        }

        failure = null;
        return links;
    }

    internal static MermaidDiagnostic[] ParseDiagnostics(
        ReadOnlySpan<byte> buffer,
        Section section,
        string[] strings,
        MermaidSceneSourceMapping[] mappings,
        CancellationToken cancellationToken,
        out MermaidSceneDecodeResult? failure)
    {
        var diagnostics = new MermaidDiagnostic[section.Count];
        for (int i = 0; i < diagnostics.Length; i++)
        {
            ThrowIfCancellationRequestedPeriodically(cancellationToken, i);
            int offset = checked(section.Offset + (i * section.Stride));
            uint severity = ReadUInt32(buffer, offset);
            uint code = ReadUInt32(buffer, offset + 4);
            uint message = ReadUInt32(buffer, offset + 8);
            uint mapping = ReadUInt32(buffer, offset + 12);
            if (severity > (uint)MermaidDiagnosticSeverity.Error || !IsRequiredIndex(code, strings.Length) ||
                !IsRequiredIndex(message, strings.Length) || !IsOptionalIndex(mapping, mappings.Length) ||
                ReadUInt32(buffer, offset + 16) != 0 || ReadUInt32(buffer, offset + 20) != 0)
            {
                failure = Failure(MermaidSceneDecodeStatus.InvalidReference, offset, "A diagnostic record contains an invalid reference.");
                return [];
            }

            MermaidSourceRange? range = mapping == NoIndex ? null : mappings[checked((int)mapping)].SourceRange;
            diagnostics[i] = new MermaidDiagnostic(strings[checked((int)code)], (MermaidDiagnosticSeverity)severity, strings[checked((int)message)], range);
        }

        failure = null;
        return diagnostics;
    }

    internal static RawSourceMapping[] ParseRawSourceMappings(
        ReadOnlySpan<byte> buffer,
        Section section,
        int sourceByteLength,
        CancellationToken cancellationToken,
        out MermaidSceneDecodeResult? failure)
    {
        var mappings = new RawSourceMapping[section.Count];
        var sourceIds = new HashSet<uint>();
        for (int i = 0; i < mappings.Length; i++)
        {
            ThrowIfCancellationRequestedPeriodically(cancellationToken, i);
            int offset = checked(section.Offset + (i * section.Stride));
            uint id = ReadUInt32(buffer, offset);
            uint start = ReadUInt32(buffer, offset + 4);
            uint length = ReadUInt32(buffer, offset + 8);
            uint semantic = ReadUInt32(buffer, offset + 12);
            if (!sourceIds.Add(id) || !IsUnsignedRangeInBounds(start, length, sourceByteLength) ||
                ReadUInt32(buffer, offset + 16) != 0)
            {
                failure = Failure(MermaidSceneDecodeStatus.InvalidSourceMapping, offset, "A source mapping is duplicated or outside the UTF-8 source.");
                return [];
            }

            mappings[i] = new RawSourceMapping(id, checked((int)start), checked((int)length), semantic);
        }

        failure = null;
        return mappings;
    }

    private static bool HasValidGeometryArity(MermaidDrawOpcode opcode, uint count, ushort minor) => opcode switch
    {
        MermaidDrawOpcode.GroupBegin or MermaidDrawOpcode.GroupEnd or MermaidDrawOpcode.ClipEnd => count == 0,
        MermaidDrawOpcode.Line or MermaidDrawOpcode.Rectangle or MermaidDrawOpcode.Ellipse => count == 4,
        MermaidDrawOpcode.Text => minor >= 1 ? count == 4 : count >= 2,
        MermaidDrawOpcode.ClipBegin or MermaidDrawOpcode.FillPath or MermaidDrawOpcode.StrokePath => count >= 2 && (count & 1) == 0,
        _ => false,
    };
}

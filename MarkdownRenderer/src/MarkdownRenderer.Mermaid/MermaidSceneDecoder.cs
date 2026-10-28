using System.Buffers.Binary;
using System.Text;
using static MarkdownRenderer.Mermaid.MmirFormat;

namespace MarkdownRenderer.Mermaid;

/// <summary>Stable failure categories returned when an untrusted MMIR buffer cannot be consumed.</summary>
public enum MermaidSceneDecodeStatus
{
    Success,
    InvalidBudget,
    BufferTooSmall,
    BadMagic,
    UnsupportedVersion,
    InvalidHeader,
    SizeMismatch,
    BudgetExceeded,
    InvalidDirectory,
    DuplicateSection,
    OverlappingSections,
    MissingSection,
    InvalidSection,
    InvalidUtf8,
    InvalidReference,
    NonFiniteNumber,
    InvalidSourceMapping,
}

/// <summary>A non-throwing result from decoding an untrusted MMIR buffer.</summary>
public sealed class MermaidSceneDecodeResult
{
    internal MermaidSceneDecodeResult(
        MermaidSceneDecodeStatus status,
        MermaidScene? scene,
        int errorOffset,
        string? error)
    {
        Status = status;
        Scene = scene;
        ErrorOffset = errorOffset;
        Error = error;
    }

    public MermaidSceneDecodeStatus Status { get; }

    public MermaidScene? Scene { get; }

    public int ErrorOffset { get; }

    public string? Error { get; }

    public bool IsSuccess => Status == MermaidSceneDecodeStatus.Success && Scene is not null;
}

/// <summary>Decodes the private little-endian MMIR transport into an immutable managed scene.</summary>
public static class MermaidSceneDecoder
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>
    /// Decodes a complete MMIR buffer. This method treats both inputs as untrusted and reports format errors
    /// without throwing. Native UTF-8 offsets are translated to public half-open UTF-16 ranges.
    /// </summary>
    public static MermaidSceneDecodeResult Decode(
        ReadOnlySpan<byte> buffer,
        string source,
        MermaidRenderBudgets? budgets = null)
        => Decode(buffer, source, budgets, CancellationToken.None);

    /// <summary>
    /// Decodes a complete MMIR buffer while cooperatively observing cancellation
    /// between bounded validation and materialization batches.
    /// </summary>
    public static MermaidSceneDecodeResult Decode(
        ReadOnlySpan<byte> buffer,
        string source,
        MermaidRenderBudgets? budgets,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        budgets ??= MermaidRenderBudgets.Default;
        string? budgetError = budgets.Validate(allowIncreases: true);
        if (budgetError is not null)
        {
            return Failure(MermaidSceneDecodeStatus.InvalidBudget, 0, budgetError);
        }

        if (source is null)
        {
            return Failure(MermaidSceneDecodeStatus.InvalidSourceMapping, 0, "The original source is required.");
        }

        if (source.Length > budgets.MaxSourceBytes)
        {
            return Failure(MermaidSceneDecodeStatus.BudgetExceeded, 0, "The Mermaid source exceeds MaxSourceBytes.");
        }

        if (!TryMeasureUtf8(source, cancellationToken, out int sourceByteLength))
        {
            return Failure(MermaidSceneDecodeStatus.InvalidUtf8, 0, "The source contains an unpaired UTF-16 surrogate.");
        }

        if (sourceByteLength > budgets.MaxSourceBytes)
        {
            return Failure(MermaidSceneDecodeStatus.BudgetExceeded, 0, "The Mermaid source exceeds MaxSourceBytes.");
        }

        try
        {
            return DecodeCore(buffer, source, sourceByteLength, budgets, cancellationToken);
        }
        catch (DecoderFallbackException)
        {
            return Failure(MermaidSceneDecodeStatus.InvalidUtf8, 0, "A scene string is not valid UTF-8.");
        }
        catch (OverflowException)
        {
            return Failure(MermaidSceneDecodeStatus.InvalidSection, 0, "An MMIR size calculation overflowed.");
        }
        catch (ArgumentException)
        {
            return Failure(MermaidSceneDecodeStatus.InvalidSection, 0, "An MMIR section is malformed.");
        }
        catch (IndexOutOfRangeException)
        {
            return Failure(MermaidSceneDecodeStatus.InvalidSection, 0, "An MMIR section ended unexpectedly.");
        }
    }

    private static MermaidSceneDecodeResult DecodeCore(
        ReadOnlySpan<byte> buffer,
        string source,
        int sourceByteLength,
        MermaidRenderBudgets budgets,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (buffer.Length < HeaderSize)
        {
            return Failure(MermaidSceneDecodeStatus.BufferTooSmall, buffer.Length, "The MMIR header is incomplete.");
        }

        if (ReadUInt32(buffer, 0) != Magic)
        {
            return Failure(MermaidSceneDecodeStatus.BadMagic, 0, "The MMIR magic value is invalid.");
        }

        ushort major = ReadUInt16(buffer, 4);
        ushort minor = ReadUInt16(buffer, 6);
        if (major != SupportedMajor || minor > SupportedMinor)
        {
            return Failure(MermaidSceneDecodeStatus.UnsupportedVersion, 4, $"MMIR {major}.{minor} is not supported.");
        }

        if (!TryUIntToInt(ReadUInt32(buffer, 8), out int headerSize) || headerSize != HeaderSize ||
            !TryUIntToInt(ReadUInt32(buffer, 12), out int totalSize) ||
            !TryUIntToInt(ReadUInt32(buffer, 16), out int directoryOffset) ||
            !TryUIntToInt(ReadUInt32(buffer, 20), out int sectionCount))
        {
            return Failure(MermaidSceneDecodeStatus.InvalidHeader, 8, "One or more MMIR header fields are invalid.");
        }

        if (ReadUInt32(buffer, 24) != 0 || ReadUInt32(buffer, 28) != 0)
        {
            return Failure(MermaidSceneDecodeStatus.InvalidHeader, 24, "Reserved MMIR header fields must be zero.");
        }

        if (totalSize != buffer.Length)
        {
            return Failure(MermaidSceneDecodeStatus.SizeMismatch, 12, "The MMIR total size does not match the buffer length.");
        }

        if (totalSize > budgets.MaxSceneBytes)
        {
            return Failure(MermaidSceneDecodeStatus.BudgetExceeded, 12, "The MMIR scene exceeds MaxSceneBytes.");
        }

        if (sectionCount is <= 0 or > MaximumSectionCount || directoryOffset < HeaderSize || (directoryOffset & 3) != 0)
        {
            return Failure(MermaidSceneDecodeStatus.InvalidDirectory, 16, "The MMIR section directory is invalid.");
        }

        int directoryLength = checked(sectionCount * SectionDescriptorSize);
        if (!IsRangeInBounds(directoryOffset, directoryLength, totalSize))
        {
            return Failure(MermaidSceneDecodeStatus.InvalidDirectory, directoryOffset, "The section directory extends past the buffer.");
        }

        int payloadFloor = checked(directoryOffset + directoryLength);
        var sections = new Section[sectionCount];
        var sectionKinds = new HashSet<uint>();

        for (int i = 0; i < sectionCount; i++)
        {
            ThrowIfCancellationRequestedPeriodically(cancellationToken, i);
            int descriptorOffset = checked(directoryOffset + (i * SectionDescriptorSize));
            uint rawKind = ReadUInt32(buffer, descriptorOffset);
            if (rawKind is < (uint)SectionKind.Viewport or > (uint)SectionKind.SourceMappings)
            {
                return Failure(MermaidSceneDecodeStatus.InvalidDirectory, descriptorOffset, "The MMIR section kind is not defined by this format version.");
            }

            if (!sectionKinds.Add(rawKind))
            {
                return Failure(MermaidSceneDecodeStatus.DuplicateSection, descriptorOffset, "An MMIR section kind appears more than once.");
            }

            if (!TryUIntToInt(ReadUInt32(buffer, descriptorOffset + 4), out int offset) ||
                !TryUIntToInt(ReadUInt32(buffer, descriptorOffset + 8), out int length) ||
                !TryUIntToInt(ReadUInt32(buffer, descriptorOffset + 12), out int count) ||
                !TryUIntToInt(ReadUInt32(buffer, descriptorOffset + 16), out int stride))
            {
                return Failure(MermaidSceneDecodeStatus.InvalidDirectory, descriptorOffset + 4, "A section field exceeds the managed address range.");
            }

            if (offset < payloadFloor || (offset & 3) != 0 || !IsRangeInBounds(offset, length, totalSize))
            {
                return Failure(MermaidSceneDecodeStatus.InvalidDirectory, descriptorOffset + 4, "A section range is invalid or not four-byte aligned.");
            }

            if (count < 0 || stride < 0 || (stride != 0 && checked((long)count * stride) != length))
            {
                return Failure(MermaidSceneDecodeStatus.InvalidDirectory, descriptorOffset + 12, "A section count, stride, or byte length is inconsistent.");
            }

            sections[i] = new Section((SectionKind)rawKind, offset, length, count, stride);
        }

        Array.Sort(sections, static (left, right) => left.Offset.CompareTo(right.Offset));
        int previousEnd = payloadFloor;
        foreach (Section section in sections)
        {
            if (section.Length > 0 && section.Offset < previousEnd)
            {
                return Failure(MermaidSceneDecodeStatus.OverlappingSections, section.Offset, "MMIR payload sections overlap.");
            }

            if (section.Length > 0)
            {
                previousEnd = checked(section.Offset + section.Length);
            }
        }

        if (!TryGetSection(sections, SectionKind.Viewport, out Section viewportSection) ||
            !TryGetSection(sections, SectionKind.Strings, out Section stringsSection) ||
            !TryGetSection(sections, SectionKind.DrawCommands, out Section commandsSection))
        {
            return Failure(MermaidSceneDecodeStatus.MissingSection, directoryOffset, "Viewport, strings, and draw-command sections are required.");
        }

        MermaidSceneDecodeResult? shapeFailure = ValidateSectionShapes(sections, budgets, cancellationToken);
        if (shapeFailure is not null)
        {
            return shapeFailure;
        }

        if (!TryParseViewport(buffer, viewportSection, out MermaidViewport viewport, out MermaidSceneDecodeResult? failure))
        {
            return failure!;
        }

        if (!TryParseStrings(buffer, stringsSection, budgets, cancellationToken, out string[] strings, out failure))
        {
            return failure!;
        }

        float[] geometry = TryGetSection(sections, SectionKind.Geometry, out Section geometrySection)
            ? ParseGeometry(buffer, geometrySection, cancellationToken, out failure)
            : [];
        if (failure is not null)
        {
            return failure;
        }

        MermaidSceneStyle[] styles = TryGetSection(sections, SectionKind.Styles, out Section stylesSection)
            ? ParseStyles(buffer, stylesSection, geometry.Length, cancellationToken, out failure)
            : [];
        if (failure is not null)
        {
            return failure;
        }

        RawSourceMapping[] rawMappings = TryGetSection(sections, SectionKind.SourceMappings, out Section mappingsSection)
            ? ParseRawSourceMappings(buffer, mappingsSection, sourceByteLength, cancellationToken, out failure)
            : [];
        if (failure is not null)
        {
            return failure;
        }

        MermaidSceneSourceMapping[] mappings = TranslateSourceMappings(
            rawMappings,
            source,
            sourceByteLength,
            cancellationToken,
            out failure);
        if (failure is not null)
        {
            return failure;
        }

        MermaidSemanticItem[] semantics = TryGetSection(sections, SectionKind.Semantics, out Section semanticsSection)
            ? ParseSemantics(
                buffer,
                semanticsSection,
                strings.Length,
                mappings.Length,
                budgets,
                cancellationToken,
                out failure)
            : [];
        if (failure is not null)
        {
            return failure;
        }

        if (!ValidateMappingSemanticReferences(mappings, semantics.Length, cancellationToken, out failure) ||
            !ValidateSemanticTree(semantics, budgets.MaxDepth, cancellationToken, out failure, out _))
        {
            return failure!;
        }

        MermaidDrawCommand[] commands = ParseCommands(
            buffer,
            commandsSection,
            minor,
            styles.Length,
            semantics.Length,
            strings.Length,
            geometry,
            cancellationToken,
            out failure);
        if (failure is not null)
        {
            return failure;
        }

        MermaidSceneLink[] links = TryGetSection(sections, SectionKind.Links, out Section linksSection)
            ? ParseLinks(buffer, linksSection, semantics.Length, strings.Length, cancellationToken, out failure)
            : [];
        if (failure is not null)
        {
            return failure;
        }

        MermaidDiagnostic[] diagnostics = TryGetSection(sections, SectionKind.Diagnostics, out Section diagnosticsSection)
            ? ParseDiagnostics(buffer, diagnosticsSection, strings, mappings, cancellationToken, out failure)
            : [];
        if (failure is not null)
        {
            return failure;
        }

        var scene = new MermaidScene(
            new MermaidSceneVersion(major, minor),
            viewport,
            strings,
            styles,
            geometry,
            commands,
            semantics,
            links,
            mappings,
            diagnostics);
        return new MermaidSceneDecodeResult(MermaidSceneDecodeStatus.Success, scene, -1, null);
    }

    private static MermaidSceneDecodeResult? ValidateSectionShapes(
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

    private static bool TryParseViewport(
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

    private static bool TryParseStrings(
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

    private static float[] ParseGeometry(
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

    private static MermaidSceneStyle[] ParseStyles(
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

    private static MermaidDrawCommand[] ParseCommands(
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

    private static MermaidSemanticItem[] ParseSemantics(
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

    private static MermaidSceneLink[] ParseLinks(
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

    private static MermaidDiagnostic[] ParseDiagnostics(
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

    private static RawSourceMapping[] ParseRawSourceMappings(
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

    private static MermaidSceneSourceMapping[] TranslateSourceMappings(
        RawSourceMapping[] rawMappings,
        string source,
        int sourceByteLength,
        CancellationToken cancellationToken,
        out MermaidSceneDecodeResult? failure)
    {
        if (rawMappings.Length == 0)
        {
            failure = null;
            return [];
        }

        var endpoints = new int[checked(rawMappings.Length * 2)];
        for (int i = 0; i < rawMappings.Length; i++)
        {
            ThrowIfCancellationRequestedPeriodically(cancellationToken, i);
            endpoints[i * 2] = rawMappings[i].Utf8Start;
            endpoints[(i * 2) + 1] = checked(rawMappings[i].Utf8Start + rawMappings[i].Utf8Length);
        }

        Array.Sort(endpoints);
        var utf16Offsets = new int[endpoints.Length];
        int endpointIndex = 0;
        int utf8Offset = 0;
        int utf16Offset = 0;
        while (true)
        {
            ThrowIfCancellationRequestedPeriodically(cancellationToken, utf16Offset);
            while (endpointIndex < endpoints.Length && endpoints[endpointIndex] == utf8Offset)
            {
                utf16Offsets[endpointIndex++] = utf16Offset;
            }

            if (utf16Offset == source.Length)
            {
                break;
            }

            if (!TryGetUtf8Width(source, utf16Offset, out int utf16Width, out int utf8Width))
            {
                failure = Failure(MermaidSceneDecodeStatus.InvalidUtf8, utf16Offset, "The source contains an unpaired UTF-16 surrogate.");
                return [];
            }

            utf16Offset += utf16Width;
            utf8Offset += utf8Width;
        }

        if (utf8Offset != sourceByteLength || endpointIndex != endpoints.Length)
        {
            failure = Failure(MermaidSceneDecodeStatus.InvalidSourceMapping, 0, "A native source offset is not on a UTF-8 scalar boundary.");
            return [];
        }

        var result = new MermaidSceneSourceMapping[rawMappings.Length];
        for (int i = 0; i < rawMappings.Length; i++)
        {
            ThrowIfCancellationRequestedPeriodically(cancellationToken, i);
            int startPosition = Array.BinarySearch(endpoints, rawMappings[i].Utf8Start);
            int endPosition = Array.BinarySearch(endpoints, checked(rawMappings[i].Utf8Start + rawMappings[i].Utf8Length));
            if (startPosition < 0 || endPosition < 0)
            {
                failure = Failure(MermaidSceneDecodeStatus.InvalidSourceMapping, 0, "A source mapping could not be translated.");
                return [];
            }

            int utf16Start = utf16Offsets[startPosition];
            int utf16End = utf16Offsets[endPosition];
            result[i] = new MermaidSceneSourceMapping(
                rawMappings[i].SourceId,
                new MermaidSourceRange(utf16Start, utf16End - utf16Start),
                OptionalIndex(rawMappings[i].SemanticIndex));
        }

        failure = null;
        return result;
    }

    private static bool ValidateMappingSemanticReferences(
        MermaidSceneSourceMapping[] mappings,
        int semanticCount,
        CancellationToken cancellationToken,
        out MermaidSceneDecodeResult? failure)
    {
        for (int i = 0; i < mappings.Length; i++)
        {
            ThrowIfCancellationRequestedPeriodically(cancellationToken, i);
            if (mappings[i].SemanticIndex < -1 || mappings[i].SemanticIndex >= semanticCount)
            {
                failure = Failure(MermaidSceneDecodeStatus.InvalidReference, i, "A source mapping references an invalid semantic item.");
                return false;
            }
        }

        failure = null;
        return true;
    }

    internal static bool ValidateSemanticTree(
        MermaidSemanticItem[] semantics,
        int maxDepth,
        CancellationToken cancellationToken,
        out MermaidSceneDecodeResult? failure,
        out int parentTraversalCount)
    {
        // Zero means unresolved, -1 means active in the current traversal, and a
        // positive value is the memoized one-based depth. The traversal buffer is
        // bounded by MaxDepth; a longer unresolved path is already over budget.
        var resolvedDepths = new int[semantics.Length];
        var traversal = new int[Math.Min(semantics.Length, maxDepth)];
        parentTraversalCount = 0;

        for (int i = 0; i < semantics.Length; i++)
        {
            ThrowIfCancellationRequestedPeriodically(cancellationToken, i);
            if (resolvedDepths[i] > 0)
            {
                continue;
            }

            int current = i;
            int traversalLength = 0;
            while (current >= 0 && resolvedDepths[current] == 0)
            {
                ThrowIfCancellationRequestedPeriodically(cancellationToken, parentTraversalCount);
                if (traversalLength == traversal.Length)
                {
                    failure = Failure(MermaidSceneDecodeStatus.BudgetExceeded, i, "The semantic tree exceeds MaxDepth.");
                    return false;
                }

                resolvedDepths[current] = -1;
                traversal[traversalLength++] = current;
                current = semantics[current].ParentIndex;
                parentTraversalCount++;
            }

            if (current >= 0 && resolvedDepths[current] < 0)
            {
                failure = Failure(MermaidSceneDecodeStatus.InvalidReference, i, "The semantic parent graph contains a cycle.");
                return false;
            }

            int depth = current >= 0 ? resolvedDepths[current] : 0;
            while (traversalLength > 0)
            {
                ThrowIfCancellationRequestedPeriodically(cancellationToken, traversalLength);
                if (depth >= maxDepth)
                {
                    failure = Failure(MermaidSceneDecodeStatus.BudgetExceeded, i, "The semantic tree exceeds MaxDepth.");
                    return false;
                }

                int semanticIndex = traversal[--traversalLength];
                resolvedDepths[semanticIndex] = ++depth;
            }
        }

        failure = null;
        return true;
    }

    private static bool HasValidGeometryArity(MermaidDrawOpcode opcode, uint count, ushort minor) => opcode switch
    {
        MermaidDrawOpcode.GroupBegin or MermaidDrawOpcode.GroupEnd or MermaidDrawOpcode.ClipEnd => count == 0,
        MermaidDrawOpcode.Line or MermaidDrawOpcode.Rectangle or MermaidDrawOpcode.Ellipse => count == 4,
        MermaidDrawOpcode.Text => minor >= 1 ? count == 4 : count >= 2,
        MermaidDrawOpcode.ClipBegin or MermaidDrawOpcode.FillPath or MermaidDrawOpcode.StrokePath => count >= 2 && (count & 1) == 0,
        _ => false,
    };

    private static bool TryGetSection(Section[] sections, SectionKind kind, out Section section)
    {
        foreach (Section candidate in sections)
        {
            if (candidate.Kind == kind)
            {
                section = candidate;
                return true;
            }
        }

        section = default;
        return false;
    }

    private static bool TryMeasureUtf8(
        string source,
        CancellationToken cancellationToken,
        out int byteLength)
    {
        long total = 0;
        for (int i = 0; i < source.Length;)
        {
            ThrowIfCancellationRequestedPeriodically(cancellationToken, i);
            if (!TryGetUtf8Width(source, i, out int utf16Width, out int utf8Width))
            {
                byteLength = 0;
                return false;
            }

            total += utf8Width;
            if (total > int.MaxValue)
            {
                byteLength = 0;
                return false;
            }

            i += utf16Width;
        }

        byteLength = (int)total;
        return true;
    }

    private static void ThrowIfCancellationRequestedPeriodically(
        CancellationToken cancellationToken,
        int index)
    {
        if ((index & 0xff) == 0)
            cancellationToken.ThrowIfCancellationRequested();
    }

    private static bool TryGetUtf8Width(string source, int index, out int utf16Width, out int utf8Width)
    {
        char value = source[index];
        if (char.IsHighSurrogate(value))
        {
            if (index + 1 >= source.Length || !char.IsLowSurrogate(source[index + 1]))
            {
                utf16Width = 0;
                utf8Width = 0;
                return false;
            }

            utf16Width = 2;
            utf8Width = 4;
            return true;
        }

        if (char.IsLowSurrogate(value))
        {
            utf16Width = 0;
            utf8Width = 0;
            return false;
        }

        utf16Width = 1;
        utf8Width = value switch
        {
            <= '\u007f' => 1,
            <= '\u07ff' => 2,
            _ => 3,
        };
        return true;
    }

    private static bool IsRangeInBounds(int offset, int length, int limit) =>
        offset >= 0 && length >= 0 && offset <= limit && length <= limit - offset;

    private static bool IsUnsignedRangeInBounds(uint offset, uint length, int limit) =>
        offset <= (uint)limit && length <= (uint)limit - offset;

    private static bool IsRequiredIndex(uint index, int count) => index < (uint)count;

    private static bool IsOptionalIndex(uint index, int count) => index == NoIndex || IsRequiredIndex(index, count);

    private static int OptionalIndex(uint index) => index == NoIndex ? -1 : checked((int)index);

    private static bool TryUIntToInt(uint value, out int result)
    {
        if (value > int.MaxValue)
        {
            result = 0;
            return false;
        }

        result = (int)value;
        return true;
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> buffer, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset, 2));

    private static uint ReadUInt32(ReadOnlySpan<byte> buffer, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(offset, 4));

    private static float ReadSingle(ReadOnlySpan<byte> buffer, int offset) =>
        BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(offset, 4)));

    private static bool AreFinite(float first, float second, float third, float fourth) =>
        float.IsFinite(first) && float.IsFinite(second) && float.IsFinite(third) && float.IsFinite(fourth);

    private static bool IsZero(ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
        {
            if (value != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static MermaidSceneDecodeResult Failure(MermaidSceneDecodeStatus status, int offset, string message) =>
        new(status, null, offset, message);

    private readonly record struct RawSourceMapping(uint SourceId, int Utf8Start, int Utf8Length, uint SemanticIndex);
}

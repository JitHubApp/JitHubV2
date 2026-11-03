using System.Text;
using static MarkdownRenderer.Mermaid.MmirFormat;
using static MarkdownRenderer.Mermaid.MmirPrimitives;
using static MarkdownRenderer.Mermaid.MermaidSceneSectionParser;

namespace MarkdownRenderer.Mermaid;

/// <summary>Decodes the private little-endian MMIR transport into an immutable managed scene.</summary>
internal static class MermaidSceneDecoder
{
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

        if (!MermaidSceneSourceText.TryMeasureUtf8(source, cancellationToken, out int sourceByteLength))
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
        MermaidSceneDecodeResult? failure = TryReadHeader(
            buffer,
            budgets,
            out ushort major,
            out ushort minor,
            out int totalSize,
            out int directoryOffset,
            out int sectionCount);
        if (failure is not null)
        {
            return failure;
        }

        failure = TryReadSectionDirectory(
            buffer,
            totalSize,
            directoryOffset,
            sectionCount,
            cancellationToken,
            out Section[] sections);
        if (failure is not null)
        {
            return failure;
        }

        return DecodeSections(
            buffer,
            source,
            sourceByteLength,
            budgets,
            major,
            minor,
            directoryOffset,
            sections,
            cancellationToken);
    }

    private static MermaidSceneDecodeResult? TryReadHeader(
        ReadOnlySpan<byte> buffer,
        MermaidRenderBudgets budgets,
        out ushort major,
        out ushort minor,
        out int totalSize,
        out int directoryOffset,
        out int sectionCount)
    {
        major = 0;
        minor = 0;
        totalSize = 0;
        directoryOffset = 0;
        sectionCount = 0;
        if (buffer.Length < HeaderSize)
        {
            return Failure(MermaidSceneDecodeStatus.BufferTooSmall, buffer.Length, "The MMIR header is incomplete.");
        }

        if (ReadUInt32(buffer, 0) != Magic)
        {
            return Failure(MermaidSceneDecodeStatus.BadMagic, 0, "The MMIR magic value is invalid.");
        }

        major = ReadUInt16(buffer, 4);
        minor = ReadUInt16(buffer, 6);
        if (major != SupportedMajor || minor > SupportedMinor)
        {
            return Failure(MermaidSceneDecodeStatus.UnsupportedVersion, 4, $"MMIR {major}.{minor} is not supported.");
        }

        if (!TryUIntToInt(ReadUInt32(buffer, 8), out int headerSize) || headerSize != HeaderSize ||
            !TryUIntToInt(ReadUInt32(buffer, 12), out totalSize) ||
            !TryUIntToInt(ReadUInt32(buffer, 16), out directoryOffset) ||
            !TryUIntToInt(ReadUInt32(buffer, 20), out sectionCount))
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

        return null;
    }

    private static MermaidSceneDecodeResult? TryReadSectionDirectory(
        ReadOnlySpan<byte> buffer,
        int totalSize,
        int directoryOffset,
        int sectionCount,
        CancellationToken cancellationToken,
        out Section[] sections)
    {
        sections = [];
        int directoryLength = checked(sectionCount * SectionDescriptorSize);
        if (!IsRangeInBounds(directoryOffset, directoryLength, totalSize))
        {
            return Failure(MermaidSceneDecodeStatus.InvalidDirectory, directoryOffset, "The section directory extends past the buffer.");
        }

        int payloadFloor = checked(directoryOffset + directoryLength);
        var directory = new Section[sectionCount];
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

            directory[i] = new Section((SectionKind)rawKind, offset, length, count, stride);
        }

        Array.Sort(directory, static (left, right) => left.Offset.CompareTo(right.Offset));
        int previousEnd = payloadFloor;
        foreach (Section section in directory)
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

        sections = directory;
        return null;
    }

    private static MermaidSceneDecodeResult DecodeSections(
        ReadOnlySpan<byte> buffer,
        string source,
        int sourceByteLength,
        MermaidRenderBudgets budgets,
        ushort major,
        ushort minor,
        int directoryOffset,
        Section[] sections,
        CancellationToken cancellationToken)
    {
        if (!TryGetSection(sections, SectionKind.Viewport, out Section viewportSection) ||
            !TryGetSection(sections, SectionKind.Strings, out Section stringsSection) ||
            !TryGetSection(sections, SectionKind.DrawCommands, out Section commandsSection))
        {
            return Failure(MermaidSceneDecodeStatus.MissingSection, directoryOffset, "Viewport, strings, and draw-command sections are required.");
        }

        MermaidSceneDecodeResult? failure = ValidateSectionShapes(sections, budgets, cancellationToken);
        if (failure is not null)
        {
            return failure;
        }

        failure = TryDecodeSceneResources(
            buffer, sections, viewportSection, stringsSection, budgets, cancellationToken,
            out MermaidViewport viewport, out string[] strings, out float[] geometry, out MermaidSceneStyle[] styles);
        if (failure is not null)
        {
            return failure;
        }

        failure = TryDecodeSourceSemantics(
            buffer, sections, source, sourceByteLength, strings.Length, budgets, cancellationToken,
            out MermaidSceneSourceMapping[] mappings, out MermaidSemanticItem[] semantics);
        if (failure is not null)
        {
            return failure;
        }

        failure = TryDecodeSceneContent(
            buffer, sections, commandsSection, minor, strings, geometry, styles.Length, semantics.Length, mappings, cancellationToken,
            out MermaidDrawCommand[] commands, out MermaidSceneLink[] links, out MermaidDiagnostic[] diagnostics);
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

    /// <summary>Decodes the viewport, string table, geometry pool and styles that commands reference.</summary>
    private static MermaidSceneDecodeResult? TryDecodeSceneResources(
        ReadOnlySpan<byte> buffer,
        Section[] sections,
        Section viewportSection,
        Section stringsSection,
        MermaidRenderBudgets budgets,
        CancellationToken cancellationToken,
        out MermaidViewport viewport,
        out string[] strings,
        out float[] geometry,
        out MermaidSceneStyle[] styles)
    {
        geometry = [];
        styles = [];
        if (!TryParseViewport(buffer, viewportSection, out viewport, out MermaidSceneDecodeResult? failure))
        {
            strings = [];
            return failure!;
        }

        if (!TryParseStrings(buffer, stringsSection, budgets, cancellationToken, out strings, out failure))
        {
            return failure!;
        }

        geometry = TryGetSection(sections, SectionKind.Geometry, out Section geometrySection)
            ? ParseGeometry(buffer, geometrySection, cancellationToken, out failure)
            : [];
        if (failure is not null)
        {
            return failure;
        }

        styles = TryGetSection(sections, SectionKind.Styles, out Section stylesSection)
            ? ParseStyles(buffer, stylesSection, geometry.Length, cancellationToken, out failure)
            : [];
        return failure;
    }

    /// <summary>
    /// Decodes source mappings into UTF-16 ranges, then the semantic items, and
    /// validates the references between them and the semantic tree shape.
    /// </summary>
    private static MermaidSceneDecodeResult? TryDecodeSourceSemantics(
        ReadOnlySpan<byte> buffer,
        Section[] sections,
        string source,
        int sourceByteLength,
        int stringCount,
        MermaidRenderBudgets budgets,
        CancellationToken cancellationToken,
        out MermaidSceneSourceMapping[] mappings,
        out MermaidSemanticItem[] semantics)
    {
        mappings = [];
        semantics = [];
        MermaidSceneDecodeResult? failure = null;
        RawSourceMapping[] rawMappings = TryGetSection(sections, SectionKind.SourceMappings, out Section mappingsSection)
            ? ParseRawSourceMappings(buffer, mappingsSection, sourceByteLength, cancellationToken, out failure)
            : [];
        if (failure is not null)
        {
            return failure;
        }

        mappings = MermaidSceneSourceText.TranslateSourceMappings(
            rawMappings,
            source,
            sourceByteLength,
            cancellationToken,
            out failure);
        if (failure is not null)
        {
            return failure;
        }

        semantics = TryGetSection(sections, SectionKind.Semantics, out Section semanticsSection)
            ? ParseSemantics(
                buffer,
                semanticsSection,
                stringCount,
                mappings.Length,
                budgets,
                cancellationToken,
                out failure)
            : [];
        if (failure is not null)
        {
            return failure;
        }

        if (!MermaidSceneSemanticValidator.ValidateMappingSemanticReferences(mappings, semantics.Length, cancellationToken, out failure) ||
            !MermaidSceneSemanticValidator.ValidateSemanticTree(semantics, budgets.MaxDepth, cancellationToken, out failure, out _))
        {
            return failure!;
        }

        return null;
    }

    /// <summary>Decodes the draw commands, links and diagnostics against the already-decoded tables.</summary>
    private static MermaidSceneDecodeResult? TryDecodeSceneContent(
        ReadOnlySpan<byte> buffer,
        Section[] sections,
        Section commandsSection,
        ushort minor,
        string[] strings,
        float[] geometry,
        int styleCount,
        int semanticCount,
        MermaidSceneSourceMapping[] mappings,
        CancellationToken cancellationToken,
        out MermaidDrawCommand[] commands,
        out MermaidSceneLink[] links,
        out MermaidDiagnostic[] diagnostics)
    {
        links = [];
        diagnostics = [];
        commands = ParseCommands(
            buffer,
            commandsSection,
            minor,
            styleCount,
            semanticCount,
            strings.Length,
            geometry,
            cancellationToken,
            out MermaidSceneDecodeResult? failure);
        if (failure is not null)
        {
            return failure;
        }

        links = TryGetSection(sections, SectionKind.Links, out Section linksSection)
            ? ParseLinks(buffer, linksSection, semanticCount, strings.Length, cancellationToken, out failure)
            : [];
        if (failure is not null)
        {
            return failure;
        }

        diagnostics = TryGetSection(sections, SectionKind.Diagnostics, out Section diagnosticsSection)
            ? ParseDiagnostics(buffer, diagnosticsSection, strings, mappings, cancellationToken, out failure)
            : [];
        return failure;
    }

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
}

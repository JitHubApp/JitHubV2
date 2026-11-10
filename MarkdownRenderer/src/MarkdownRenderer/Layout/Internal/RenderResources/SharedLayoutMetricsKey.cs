using System;
using System.Runtime.CompilerServices;

namespace MarkdownRenderer.Layout.Internal.RenderResources;

/// <summary>
/// Exact identity for reusable layout metrics. Object-valued fields use
/// reference identity because host services may have mutable behavior and must
/// never accidentally share geometry merely because they implement value
/// equality.
/// </summary>
internal sealed class SharedLayoutMetricsKey : IEquatable<SharedLayoutMetricsKey>
{
    private readonly int _hashCode;

    internal SharedLayoutMetricsKey(
        string source,
        object documentIdentity,
        int widthInSixtyFourthDips,
        ulong typographyFingerprint,
        int flowDirection,
        int codeWrappingMode,
        int lineNumberMode,
        bool codeCopyEnabled,
        bool taskEditingEnabled,
        int rasterScaleInThousandths,
        string? language,
        int registryRevision,
        ulong disclosureFingerprint,
        SharedLayoutImageContext imageContext,
        object? styleSheet,
        object? registry,
        object? embedFactory,
        object? hostedElementFactory,
        object? imageResolver,
        object? svgRenderer,
        object? rasterDecoder,
        object? commandProvider,
        object? stringProvider)
    {
        // Document identity already uniquely identifies the immutable source.
        // Retaining and hashing the source here duplicated the largest string in
        // the key and put an O(source length) operation on every UI relayout.
        _ = source;
        DocumentIdentity = documentIdentity ?? throw new ArgumentNullException(nameof(documentIdentity));
        WidthInSixtyFourthDips = widthInSixtyFourthDips;
        TypographyFingerprint = typographyFingerprint;
        FlowDirection = flowDirection;
        CodeWrappingMode = codeWrappingMode;
        LineNumberMode = lineNumberMode;
        CodeCopyEnabled = codeCopyEnabled;
        TaskEditingEnabled = taskEditingEnabled;
        RasterScaleInThousandths = rasterScaleInThousandths;
        Language = language ?? string.Empty;
        RegistryRevision = registryRevision;
        DisclosureFingerprint = disclosureFingerprint;
        ImageContext = imageContext;
        StyleSheet = styleSheet;
        Registry = registry;
        EmbedFactory = embedFactory;
        HostedElementFactory = hostedElementFactory;
        ImageResolver = imageResolver;
        SvgRenderer = svgRenderer;
        RasterDecoder = rasterDecoder;
        CommandProvider = commandProvider;
        StringProvider = stringProvider;

        var hash = new HashCode();
        AddReferenceHash(ref hash, documentIdentity);
        hash.Add(widthInSixtyFourthDips);
        hash.Add(typographyFingerprint);
        hash.Add(flowDirection);
        hash.Add(codeWrappingMode);
        hash.Add(lineNumberMode);
        hash.Add(codeCopyEnabled);
        hash.Add(taskEditingEnabled);
        hash.Add(rasterScaleInThousandths);
        hash.Add(Language, StringComparer.Ordinal);
        hash.Add(registryRevision);
        hash.Add(disclosureFingerprint);
        hash.Add(imageContext);
        AddReferenceHash(ref hash, styleSheet);
        AddReferenceHash(ref hash, registry);
        AddReferenceHash(ref hash, embedFactory);
        AddReferenceHash(ref hash, hostedElementFactory);
        AddReferenceHash(ref hash, imageResolver);
        AddReferenceHash(ref hash, svgRenderer);
        AddReferenceHash(ref hash, rasterDecoder);
        AddReferenceHash(ref hash, commandProvider);
        AddReferenceHash(ref hash, stringProvider);
        _hashCode = hash.ToHashCode();
    }

    internal readonly object DocumentIdentity;

    internal readonly int WidthInSixtyFourthDips;

    internal readonly ulong TypographyFingerprint;

    internal readonly int FlowDirection;

    internal readonly int CodeWrappingMode;

    internal readonly int LineNumberMode;

    internal readonly bool CodeCopyEnabled;

    internal readonly bool TaskEditingEnabled;

    internal readonly int RasterScaleInThousandths;

    internal readonly string Language;

    internal readonly int RegistryRevision;

    internal readonly ulong DisclosureFingerprint;

    internal readonly SharedLayoutImageContext ImageContext;

    internal readonly object? StyleSheet;

    internal readonly object? Registry;

    internal readonly object? EmbedFactory;

    internal readonly object? HostedElementFactory;

    internal readonly object? ImageResolver;

    internal readonly object? SvgRenderer;

    internal readonly object? RasterDecoder;

    internal readonly object? CommandProvider;

    internal readonly object? StringProvider;

    internal long EstimatedKeyWeightBytes => 256L;

    public bool Equals(SharedLayoutMetricsKey? other) =>
        other is not null &&
        _hashCode == other._hashCode &&
        WidthInSixtyFourthDips == other.WidthInSixtyFourthDips &&
        TypographyFingerprint == other.TypographyFingerprint &&
        FlowDirection == other.FlowDirection &&
        CodeWrappingMode == other.CodeWrappingMode &&
        LineNumberMode == other.LineNumberMode &&
        CodeCopyEnabled == other.CodeCopyEnabled &&
        TaskEditingEnabled == other.TaskEditingEnabled &&
        RasterScaleInThousandths == other.RasterScaleInThousandths &&
        string.Equals(Language, other.Language, StringComparison.Ordinal) &&
        RegistryRevision == other.RegistryRevision &&
        DisclosureFingerprint == other.DisclosureFingerprint &&
        ImageContext.Equals(other.ImageContext) &&
        ReferenceEquals(DocumentIdentity, other.DocumentIdentity) &&
        ReferenceEquals(StyleSheet, other.StyleSheet) &&
        ReferenceEquals(Registry, other.Registry) &&
        ReferenceEquals(EmbedFactory, other.EmbedFactory) &&
        ReferenceEquals(HostedElementFactory, other.HostedElementFactory) &&
        ReferenceEquals(ImageResolver, other.ImageResolver) &&
        ReferenceEquals(SvgRenderer, other.SvgRenderer) &&
        ReferenceEquals(RasterDecoder, other.RasterDecoder) &&
        ReferenceEquals(CommandProvider, other.CommandProvider) &&
        ReferenceEquals(StringProvider, other.StringProvider);

    public override bool Equals(object? obj) =>
        obj is SharedLayoutMetricsKey other && Equals(other);

    public override int GetHashCode() => _hashCode;

    private static void AddReferenceHash(ref HashCode hash, object? value) =>
        hash.Add(value is null ? 0 : RuntimeHelpers.GetHashCode(value));
}

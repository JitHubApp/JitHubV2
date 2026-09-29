using System;
using System.Runtime.CompilerServices;
using System.Threading;
using MarkdownRenderer.Images;
using MarkdownRenderer.Utilities;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Engine-scoped cache of device-independent, measured top-level block heights.
/// The values are hints only: every realized block is still measured by
/// DirectWrite before it is painted. Sharing these hints gives a second view of
/// the same document a stable initial extent without sharing Canvas resources.
/// </summary>
internal sealed class SharedLayoutMetricsCache
{
    internal const long DefaultBudgetBytes = 32L * 1024 * 1024;

    private static readonly ConditionalWeakTable<MarkdownEngine, SharedLayoutMetricsCache> EngineCaches = new();

    private readonly object _coordinationGate = new();
    private readonly WeightedLruCache<SharedLayoutMetricsKey, SharedLayoutMetrics> _entries;

    internal SharedLayoutMetricsCache(long budgetBytes = DefaultBudgetBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(budgetBytes);
        _entries = new WeightedLruCache<SharedLayoutMetricsKey, SharedLayoutMetrics>(
            budgetBytes,
            static metrics => metrics.EstimatedWeightBytes);
    }

    internal static SharedLayoutMetrics Acquire(
        MarkdownEngine? engine,
        SharedLayoutMetricsKey key,
        int topLevelBlockCount)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentOutOfRangeException.ThrowIfNegative(topLevelBlockCount);

        // The obsolete compatibility path can run without an engine. It still
        // receives a metrics buffer, but cannot retain or share it.
        if (engine is null)
            return new SharedLayoutMetrics(topLevelBlockCount, key.EstimatedKeyWeightBytes);

        return EngineCaches.GetValue(engine, static _ => new SharedLayoutMetricsCache())
            .GetOrCreate(key, topLevelBlockCount);
    }

    internal static (int Count, long RetainedBytes) GetStatistics(MarkdownEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        return EngineCaches.TryGetValue(engine, out SharedLayoutMetricsCache? cache)
            ? (cache._entries.Count, cache._entries.RetainedBytes)
            : default;
    }

    internal static void Clear(MarkdownEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (EngineCaches.TryGetValue(engine, out SharedLayoutMetricsCache? cache))
            cache._entries.Clear();
    }

    internal (int Count, long RetainedBytes) Statistics =>
        (_entries.Count, _entries.RetainedBytes);

    internal SharedLayoutMetrics GetOrCreate(
        SharedLayoutMetricsKey key,
        int topLevelBlockCount)
    {
        // WeightedLruCache is thread-safe, but creation must also be atomic so
        // concurrent controls update the same retained metrics buffer.
        lock (_coordinationGate)
        {
            if (_entries.TryGetValue(key, out SharedLayoutMetrics? existing) &&
                existing.TopLevelBlockCount == topLevelBlockCount)
            {
                return existing;
            }

            var created = new SharedLayoutMetrics(
                topLevelBlockCount,
                key.EstimatedKeyWeightBytes);
            _entries.Set(key, created);
            return created;
        }
    }
}

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
        AddReferenceHash(ref hash, commandProvider);
        AddReferenceHash(ref hash, stringProvider);
        _hashCode = hash.ToHashCode();
    }

    internal object DocumentIdentity { get; }

    internal int WidthInSixtyFourthDips { get; }

    internal ulong TypographyFingerprint { get; }

    internal int FlowDirection { get; }

    internal int CodeWrappingMode { get; }

    internal int LineNumberMode { get; }

    internal bool CodeCopyEnabled { get; }

    internal bool TaskEditingEnabled { get; }

    internal int RasterScaleInThousandths { get; }

    internal string Language { get; }

    internal int RegistryRevision { get; }

    internal ulong DisclosureFingerprint { get; }

    internal SharedLayoutImageContext ImageContext { get; }

    internal object? StyleSheet { get; }

    internal object? Registry { get; }

    internal object? EmbedFactory { get; }

    internal object? HostedElementFactory { get; }

    internal object? ImageResolver { get; }

    internal object? SvgRenderer { get; }

    internal object? CommandProvider { get; }

    internal object? StringProvider { get; }

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
        ReferenceEquals(CommandProvider, other.CommandProvider) &&
        ReferenceEquals(StringProvider, other.StringProvider);

    public override bool Equals(object? obj) =>
        obj is SharedLayoutMetricsKey other && Equals(other);

    public override int GetHashCode() => _hashCode;

    private static void AddReferenceHash(ref HashCode hash, object? value) =>
        hash.Add(value is null ? 0 : RuntimeHelpers.GetHashCode(value));
}

internal readonly record struct SharedLayoutImageContext(
    string? BaseUri,
    string? DocumentPath,
    bool AllowThirdPartyRemoteImages,
    MarkdownDocumentSource? DocumentSource);

/// <summary>
/// Concurrent reusable height observations. A zero slot means no observation;
/// positive finite heights are published atomically after successful measure.
/// </summary>
internal sealed class SharedLayoutMetrics
{
    private readonly float[] _heights;

    internal SharedLayoutMetrics(int topLevelBlockCount, long estimatedKeyWeightBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(topLevelBlockCount);
        _heights = topLevelBlockCount == 0
            ? Array.Empty<float>()
            : new float[topLevelBlockCount];
        EstimatedWeightBytes = AddSaturating(
            Math.Max(1, estimatedKeyWeightBytes),
            128L + ((long)_heights.Length * sizeof(float)));
    }

    internal int TopLevelBlockCount => _heights.Length;

    internal long EstimatedWeightBytes { get; }

    internal bool TryGetHeight(int ordinal, out float height)
    {
        if ((uint)ordinal >= (uint)_heights.Length)
        {
            height = 0;
            return false;
        }

        height = Volatile.Read(ref _heights[ordinal]);
        return float.IsFinite(height) && height > 0;
    }

    internal void RecordHeight(int ordinal, float height)
    {
        if ((uint)ordinal >= (uint)_heights.Length ||
            !float.IsFinite(height) ||
            height <= 0)
        {
            return;
        }

        Volatile.Write(ref _heights[ordinal], height);
    }

    private static long AddSaturating(long left, long right) =>
        left >= long.MaxValue - right ? long.MaxValue : left + right;
}

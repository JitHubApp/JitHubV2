using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.UI.Text;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Utilities;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Process-wide cache for immutable CanvasTextFormat descriptors. Formats are
/// not device resources, and callers hold leases so LRU eviction cannot dispose
/// a descriptor while a native text-layout constructor is consuming it.
/// </summary>
internal static class SharedCanvasTextFormatCache
{
    internal const long DefaultBudgetBytes = 512L * 1024;
    internal const string NativeCleanupFailureDataKey = "MarkdownRenderer.NativeCleanupFailure";
    // Keep explicit line spacing within the same generous DirectWrite text
    // dimension envelope used for vector-scene labels.
    private const float MaximumLineSpacingDips = 1_000_000f;
    private static readonly Store SharedStore = new(DefaultBudgetBytes, NativeResourceRetirement.Shared);

    internal static Lease Acquire(
        ElementStyle style,
        CanvasWordWrapping wordWrapping,
        CanvasHorizontalAlignment horizontalAlignment,
        FlowDirection flowDirection,
        string? localeName = null,
        bool useContentAwareLineSpacing = false) =>
        SharedStore.Acquire(style, wordWrapping, horizontalAlignment, flowDirection, localeName,
            useContentAwareLineSpacing, CanvasVerticalAlignment.Top);

    /// <summary>
    /// Acquires a format with an explicit vertical alignment. The original
    /// six-parameter overload remains the default-Top ABI for existing callers.
    /// </summary>
    internal static Lease Acquire(
        ElementStyle style,
        CanvasWordWrapping wordWrapping,
        CanvasHorizontalAlignment horizontalAlignment,
        FlowDirection flowDirection,
        string? localeName,
        bool useContentAwareLineSpacing,
        CanvasVerticalAlignment verticalAlignment) =>
        SharedStore.Acquire(style, wordWrapping, horizontalAlignment, flowDirection, localeName,
            useContentAwareLineSpacing, verticalAlignment);

    internal static void Clear() => SharedStore.Clear();

    internal static (int Count, long RetainedBytes) Statistics => SharedStore.Statistics;

    /// <summary>
    /// Isolated cache instance for deterministic native-lifetime tests. Fault
    /// callbacks are accepted only with a timerless retirement owner; the
    /// process-wide cache never stores arbitrary delegates.
    /// </summary>
    internal sealed class Store
    {
        private readonly object _coordinationGate = new();
        private readonly NativeResourceRetirement _retirement;
        private readonly Action<IDisposable>? _nativeDisposerForTests;
        private readonly Action<CanvasTextFormat>? _afterInitializeForTests;
        private readonly Action<Entry>? _afterEntryCreatedForTests;
        private readonly Action? _beforeCachePublicationForTests;
        private readonly bool _failLeaseConstructionForTests;
        private readonly bool _failCacheHitLeaseConstructionForTests;
        private readonly WeightedLruCache<TextFormatKey, Entry> _cache;
        private long _testRecordReservationCount;

        internal Store(
            long budgetBytes,
            NativeResourceRetirement retirement,
            Action<IDisposable>? nativeDisposerForTests = null,
            Action<CanvasTextFormat>? afterInitializeForTests = null,
            Action<Entry>? afterEntryCreatedForTests = null,
            Action? beforeCachePublicationForTests = null,
            bool failLeaseConstructionForTests = false,
            bool failCacheHitLeaseConstructionForTests = false)
        {
            ArgumentNullException.ThrowIfNull(retirement);
            if ((nativeDisposerForTests is not null || afterInitializeForTests is not null ||
                 afterEntryCreatedForTests is not null || beforeCachePublicationForTests is not null ||
                 failLeaseConstructionForTests || failCacheHitLeaseConstructionForTests) &&
                !retirement.AllowsTestDisposer)
            {
                throw new ArgumentException(
                    "Text-format fault injection requires an isolated timerless retirement owner.",
                    nameof(retirement));
            }

            _retirement = retirement;
            _nativeDisposerForTests = nativeDisposerForTests;
            _afterInitializeForTests = afterInitializeForTests;
            _afterEntryCreatedForTests = afterEntryCreatedForTests;
            _beforeCachePublicationForTests = beforeCachePublicationForTests;
            _failLeaseConstructionForTests = failLeaseConstructionForTests;
            _failCacheHitLeaseConstructionForTests = failCacheHitLeaseConstructionForTests;
            _cache = new WeightedLruCache<TextFormatKey, Entry>(
                budgetBytes,
                static entry => entry.WeightBytes,
                static entry => entry.ReleaseCacheReference(),
                TextFormatKeyComparer.Instance);
        }

        internal Lease Acquire(
            ElementStyle style,
            CanvasWordWrapping wordWrapping,
            CanvasHorizontalAlignment horizontalAlignment,
            FlowDirection flowDirection,
            string? localeName,
            bool useContentAwareLineSpacing) =>
            Acquire(style, wordWrapping, horizontalAlignment, flowDirection, localeName,
                useContentAwareLineSpacing, CanvasVerticalAlignment.Top);

        internal Lease Acquire(
            ElementStyle style,
            CanvasWordWrapping wordWrapping,
            CanvasHorizontalAlignment horizontalAlignment,
            FlowDirection flowDirection,
            string? localeName,
            bool useContentAwareLineSpacing,
            CanvasVerticalAlignment verticalAlignment)
        {
            ArgumentNullException.ThrowIfNull(style);
            // Treat non-positive, non-finite, or unrenderably large resolved
            // spacing as normal DirectWrite spacing. Zero is the cache-key
            // sentinel for that unchanged behavior; ordinary CSS line height
            // values are not clamped.
            float lineSpacing = 0f;
            if (!useContentAwareLineSpacing)
            {
                float resolvedLineSpacing = style.FontSize * style.LineHeightMultiplier;
                lineSpacing = float.IsFinite(style.LineHeightMultiplier) &&
                              style.LineHeightMultiplier > 0f &&
                              resolvedLineSpacing > 0f &&
                              resolvedLineSpacing <= MaximumLineSpacingDips
                    ? resolvedLineSpacing
                    : 0f;
            }
            var key = new TextFormatKey(
                NormalizeFontFamilyForCanvas(style.FontFamily),
                style.FontSize,
                style.FontWeight,
                style.FontStyle,
                wordWrapping,
                horizontalAlignment,
                flowDirection == FlowDirection.RightToLeft
                    ? CanvasTextDirection.RightToLeftThenTopToBottom
                    : CanvasTextDirection.LeftToRightThenTopToBottom,
                verticalAlignment,
                lineSpacing,
                string.IsNullOrWhiteSpace(localeName) ? string.Empty : localeName.Trim());

            lock (_coordinationGate)
            {
                if (_cache.TryGetValue(key, out Entry? cached) && cached.TryAcquire())
                {
                    try
                    {
                        // Cache hits never allocate retirement records. If
                        // lease allocation fails, undo only this new ref.
                        if (_failCacheHitLeaseConstructionForTests)
                            throw new InvalidOperationException("Injected cache-hit lease allocation failure.");
                        return new Lease(cached);
                    }
                    catch
                    {
                        cached.Release();
                        throw;
                    }
                }

                return CreateAndPublishNoLock(key);
            }
        }

        private Lease CreateAndPublishNoLock(TextFormatKey key)
        {
            // Reserve custody and its intrusive queue link before creating the
            // native object. Assign the format to its slot immediately after
            // the factory returns, before any setter can fail.
            if (_retirement.AllowsTestDisposer)
                Interlocked.Increment(ref _testRecordReservationCount);
            NativeResourceRetirementRecord retirementRecord = _retirement.Reserve(
                resourceGroupCount: 0,
                temporarySlotCount: 1,
                disposer: _nativeDisposerForTests);
            CanvasTextFormat? format = null;
            Entry? entry = null;
            Lease? lease = null;
            bool prospectiveCacheReference = false;
            try
            {
                format = new CanvasTextFormat();
                retirementRecord.SetTemporary(0, format);
                InitializeFormat(format, key);
                _afterInitializeForTests?.Invoke(format);

                entry = new Entry(
                    format,
                    256L + (((long)key.FontFamily.Length + key.LocaleName.Length) * sizeof(char)),
                    retirementRecord);
                _afterEntryCreatedForTests?.Invoke(entry);
                format = null; // Entry and its reserved record now own the native slot.

                if (_failLeaseConstructionForTests)
                    throw new InvalidOperationException("Injected text-format lease allocation failure.");

                lease = new Lease(entry);
                entry.AddCacheReference();
                prospectiveCacheReference = true;

                // WeightedLruCache.Set allocates all commit prerequisites before
                // mutation. The production eviction callback is non-throwing,
                // so a thrown Set is a precommit failure and both refs are ours.
                _beforeCachePublicationForTests?.Invoke();
                _cache.Set(key, entry);
                prospectiveCacheReference = false; // Set accepted/rejected and consumed the cache ref.
                return lease;
            }
            catch (Exception primaryFailure)
            {
                Exception? cleanupFailure = null;
                if (entry is not null)
                {
                    if (lease is not null)
                    {
                        if (prospectiveCacheReference)
                            entry.ReleaseCacheReference();
                        cleanupFailure = lease.ReleaseForFailure();
                    }
                    else
                    {
                        // Entry exists but Lease allocation did not complete.
                        cleanupFailure = entry.Release();
                    }
                }
                else if (format is not null)
                {
                    cleanupFailure = RetireConstructionFailure(format, retirementRecord);
                }
                if (cleanupFailure is not null)
                    AttachCleanupFailure(primaryFailure, cleanupFailure);
                throw;
            }
        }

        private static Exception? RetireConstructionFailure(
            CanvasTextFormat format,
            NativeResourceRetirementRecord retirementRecord)
        {
            // The reserved record owns the returned format already. A failed
            // cleanup publishes that exact slot while the original initializer
            // or Entry-allocation exception remains the one observed by caller.
            try
            {
                retirementRecord.Disposer(format);
                retirementRecord.SetTemporary(0, null);
                retirementRecord.Publish();
                return null;
            }
            catch (Exception cleanupFailure)
            {
                retirementRecord.Publish();
                return cleanupFailure;
            }
        }

        private static void AttachCleanupFailure(Exception primaryFailure, Exception cleanupFailure)
        {
            // Custody is published before this bounded best-effort diagnostic.
            // Never replace the exception that caused construction/publication
            // to fail, including if exception Data itself cannot allocate.
            try
            {
                if (!primaryFailure.Data.Contains(NativeCleanupFailureDataKey))
                    primaryFailure.Data[NativeCleanupFailureDataKey] = cleanupFailure;
            }
            catch
            {
            }
        }

        internal void Clear()
        {
            lock (_coordinationGate)
                _cache.Clear();
        }

        internal (int Count, long RetainedBytes) Statistics => (_cache.Count, _cache.RetainedBytes);

        internal long RecordReservationCountForTests => Interlocked.Read(ref _testRecordReservationCount);
    }

    private static void InitializeFormat(CanvasTextFormat format, TextFormatKey key)
    {
        format.FontFamily = key.FontFamily;
        format.FontSize = key.FontSize;
        format.FontWeight = key.FontWeight;
        format.FontStyle = key.FontStyle;
        format.WordWrapping = key.WordWrapping;
        format.Direction = key.Direction;
        format.HorizontalAlignment = key.HorizontalAlignment;
        format.VerticalAlignment = key.VerticalAlignment;
        if (key.LineSpacing > 0f)
        {
            // A positive LineSpacing is absolute. Text-only layouts preserve
            // CSS spacing while atomic inline objects retain DirectWrite's
            // normal line-height expansion behavior.
            format.LineSpacing = key.LineSpacing;
            format.LineSpacingBaseline = key.LineSpacing * 0.8f;
        }
        if (key.LocaleName.Length > 0)
            format.LocaleName = key.LocaleName;
    }

    /// <summary>
    /// Converts a WinUI packaged-font reference to the form accepted by Win2D.
    /// XAML needs the fragment to select a family from the font file, whereas
    /// <see cref="CanvasTextFormat"/> accepts the app-package font URI itself.
    /// </summary>
    internal static string NormalizeFontFamilyForCanvas(string fontFamily)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fontFamily);
        string candidate = fontFamily.Trim();
        int fragmentIndex = candidate.LastIndexOf('#');
        if (fragmentIndex <= 0 ||
            !Uri.TryCreate(candidate[..fragmentIndex], UriKind.Absolute, out Uri? packageFontUri) ||
            !string.Equals(packageFontUri.Scheme, "ms-appx", StringComparison.OrdinalIgnoreCase))
            return candidate;

        return candidate[..fragmentIndex];
    }

    private readonly struct TextFormatKey
    {
        internal TextFormatKey(
            string fontFamily,
            float fontSize,
            FontWeight fontWeight,
            FontStyle fontStyle,
            CanvasWordWrapping wordWrapping,
            CanvasHorizontalAlignment horizontalAlignment,
            CanvasTextDirection direction,
            CanvasVerticalAlignment verticalAlignment,
            float lineSpacing,
            string localeName)
        {
            FontFamily = fontFamily;
            FontSize = fontSize;
            FontWeight = fontWeight;
            FontStyle = fontStyle;
            WordWrapping = wordWrapping;
            HorizontalAlignment = horizontalAlignment;
            Direction = direction;
            VerticalAlignment = verticalAlignment;
            LineSpacing = lineSpacing;
            LocaleName = localeName;
        }

        internal readonly string FontFamily;
        internal readonly float FontSize;
        internal readonly FontWeight FontWeight;
        internal readonly FontStyle FontStyle;
        internal readonly CanvasWordWrapping WordWrapping;
        internal readonly CanvasHorizontalAlignment HorizontalAlignment;
        internal readonly CanvasTextDirection Direction;
        internal readonly CanvasVerticalAlignment VerticalAlignment;
        internal readonly float LineSpacing;
        internal readonly string LocaleName;
    }

    private sealed class TextFormatKeyComparer : IEqualityComparer<TextFormatKey>
    {
        internal static readonly TextFormatKeyComparer Instance = new();

        public bool Equals(TextFormatKey left, TextFormatKey right) =>
            StringComparer.Ordinal.Equals(left.FontFamily, right.FontFamily) &&
            left.FontSize.Equals(right.FontSize) &&
            left.FontWeight == right.FontWeight &&
            left.FontStyle == right.FontStyle &&
            left.WordWrapping == right.WordWrapping &&
            left.HorizontalAlignment == right.HorizontalAlignment &&
            left.Direction == right.Direction &&
            left.VerticalAlignment == right.VerticalAlignment &&
            left.LineSpacing.Equals(right.LineSpacing) &&
            StringComparer.OrdinalIgnoreCase.Equals(left.LocaleName, right.LocaleName);

        public int GetHashCode(TextFormatKey key) => HashCode.Combine(
            HashCode.Combine(
                key.FontFamily,
                key.FontSize,
                key.FontWeight,
                key.FontStyle,
                key.WordWrapping,
                key.HorizontalAlignment,
                key.Direction,
                key.VerticalAlignment),
            key.LineSpacing,
            StringComparer.OrdinalIgnoreCase.GetHashCode(key.LocaleName));
    }

    internal sealed class Entry
    {
        private readonly NativeResourceRetirementRecord _retirementRecord;
        private int _referenceCount = 1; // caller/acquisition reference

        internal Entry(CanvasTextFormat format, long weightBytes, NativeResourceRetirementRecord retirementRecord)
        {
            Format = format;
            WeightBytes = Math.Max(1, weightBytes);
            _retirementRecord = retirementRecord;
        }

        internal readonly CanvasTextFormat Format;
        internal readonly long WeightBytes;
        internal int ReferenceCountForTests => Volatile.Read(ref _referenceCount);

        internal void AddCacheReference() => Interlocked.Increment(ref _referenceCount);

        internal bool TryAcquire()
        {
            int observed = Volatile.Read(ref _referenceCount);
            while (observed > 0)
            {
                if (observed == int.MaxValue)
                    return false;
                int updated = Interlocked.CompareExchange(ref _referenceCount, observed + 1, observed);
                if (updated == observed)
                    return true;
                observed = updated;
            }
            return false;
        }

        internal void ReleaseCacheReference() => Release();

        internal Exception? Release()
        {
            if (Interlocked.Decrement(ref _referenceCount) != 0)
                return null;

            try
            {
                _retirementRecord.Disposer(Format);
                _retirementRecord.SetTemporary(0, null);
                _retirementRecord.Publish();
                return null;
            }
            catch (Exception cleanupFailure)
            {
                // A final release cannot retain Entry or its immutable fields.
                // Its preallocated source-free record already contains Format.
                _retirementRecord.Publish();
                return cleanupFailure;
            }
        }
    }

    internal sealed class Lease : IDisposable
    {
        private Entry? _entry;

        internal Lease(Entry entry) => _entry = entry;

        internal CanvasTextFormat Format =>
            Volatile.Read(ref _entry)?.Format ?? throw new ObjectDisposedException(nameof(Lease));

        // Narrow internal witness used by GC-lifetime regression tests; never
        // exposed in the public renderer API.
        internal Entry? EntryForTests => Volatile.Read(ref _entry);

        internal Exception? ReleaseForFailure() => Interlocked.Exchange(ref _entry, null)?.Release();

        public void Dispose() => _ = ReleaseForFailure();
    }
}

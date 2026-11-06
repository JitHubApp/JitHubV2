using System;
using System.Threading;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Utilities;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Bounded LRU store of immutable CanvasTextFormat descriptors with native
/// retirement custody. Each <see cref="MarkdownRenderResources"/> owns one
/// production instance; isolated instances back deterministic native-lifetime
/// tests. Fault hooks are accepted only with a timerless retirement owner, so
/// a production store never holds arbitrary delegates.
/// </summary>
internal sealed class SharedCanvasTextFormatStore
{
    // Keep explicit line spacing within the same generous DirectWrite text
    // dimension envelope used for vector-scene labels.
    private const float MaximumLineSpacingDips = 1_000_000f;

    private readonly object _coordinationGate = new();
    private readonly NativeResourceRetirement _retirement;
    private readonly SharedCanvasTextFormatStoreHooks? _hooks;
    private readonly WeightedLruCache<TextFormatKey, SharedCanvasTextFormatEntry> _cache;
    private long _recordReservationCount;

    internal SharedCanvasTextFormatStore(
        long budgetBytes,
        NativeResourceRetirement retirement,
        SharedCanvasTextFormatStoreHooks? hooks = null)
    {
        ArgumentNullException.ThrowIfNull(retirement);
        if (hooks is not null && hooks.InjectsFaults && !retirement.AllowsTestDisposer)
        {
            throw new ArgumentException(
                "Text-format fault injection requires an isolated timerless retirement owner.",
                nameof(retirement));
        }

        _retirement = retirement;
        _hooks = hooks;
        _cache = new WeightedLruCache<TextFormatKey, SharedCanvasTextFormatEntry>(
            budgetBytes,
            static entry => entry.WeightBytes,
            static entry => entry.ReleaseCacheReference(),
            TextFormatKeyComparer.Instance);
    }

    internal SharedCanvasTextFormatCache.Lease Acquire(
        ElementStyle style,
        CanvasWordWrapping wordWrapping,
        CanvasHorizontalAlignment horizontalAlignment,
        FlowDirection flowDirection,
        string? localeName = null,
        bool useContentAwareLineSpacing = false) =>
        Acquire(style, wordWrapping, horizontalAlignment, flowDirection, localeName,
            useContentAwareLineSpacing, CanvasVerticalAlignment.Top);

    internal SharedCanvasTextFormatCache.Lease Acquire(
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
            SharedCanvasTextFormatCache.NormalizeFontFamilyForCanvas(style.FontFamily),
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
            if (_cache.TryGetValue(key, out SharedCanvasTextFormatEntry? cached) && cached.TryAcquire())
            {
                try
                {
                    // Cache hits never allocate retirement records. If
                    // lease allocation fails, undo only this new ref.
                    if (_hooks?.FailCacheHitLeaseConstruction == true)
                        throw new InvalidOperationException("Injected cache-hit lease allocation failure.");
                    return new SharedCanvasTextFormatCache.Lease(cached);
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

    private SharedCanvasTextFormatCache.Lease CreateAndPublishNoLock(TextFormatKey key)
    {
        // Reserve custody and its intrusive queue link before creating the
        // native object. Assign the format to its slot immediately after
        // the factory returns, before any setter can fail.
        Interlocked.Increment(ref _recordReservationCount);
        NativeResourceRetirementRecord retirementRecord = _retirement.Reserve(
            resourceGroupCount: 0,
            temporarySlotCount: 1,
            disposer: _hooks?.NativeDisposer);
        CanvasTextFormat? format = null;
        SharedCanvasTextFormatEntry? entry = null;
        SharedCanvasTextFormatCache.Lease? lease = null;
        bool prospectiveCacheReference = false;
        try
        {
            format = new CanvasTextFormat();
            retirementRecord.SetTemporary(0, format);
            InitializeFormat(format, key);
            _hooks?.AfterInitialize?.Invoke(format);

            entry = new SharedCanvasTextFormatEntry(
                format,
                256L + (((long)key.FontFamily.Length + key.LocaleName.Length) * sizeof(char)),
                retirementRecord);
            _hooks?.AfterEntryCreated?.Invoke(entry);
            format = null; // Entry and its reserved record now own the native slot.

            if (_hooks?.FailLeaseConstruction == true)
                throw new InvalidOperationException("Injected text-format lease allocation failure.");

            lease = new SharedCanvasTextFormatCache.Lease(entry);
            entry.AddCacheReference();
            prospectiveCacheReference = true;

            // WeightedLruCache.Set allocates all commit prerequisites before
            // mutation. The production eviction callback is non-throwing,
            // so a thrown Set is a precommit failure and both refs are ours.
            _hooks?.BeforeCachePublication?.Invoke();
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
            if (!primaryFailure.Data.Contains(SharedCanvasTextFormatCache.NativeCleanupFailureDataKey))
                primaryFailure.Data[SharedCanvasTextFormatCache.NativeCleanupFailureDataKey] = cleanupFailure;
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

    /// <summary>Native retirement records reserved by cache misses since construction.</summary>
    internal long RecordReservationCount => Interlocked.Read(ref _recordReservationCount);

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
}

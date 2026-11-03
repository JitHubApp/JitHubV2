using System;
using System.Collections.Generic;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Ordinal caches of one <see cref="InlineContainerBox"/>'s effective run
/// aliases, resolved run styles and resolved nested link styles. The box owns
/// it by value, so it adds no allocation of its own; each slot list is created
/// only when its first non-default entry is recorded. Every slot list, once
/// created, has exactly one slot per run of the owning box.
/// </summary>
internal struct InlineRunStyleCache
{
    // Most runs inherit the container aliases. Keep that common case implicit;
    // once an override exists, null slots preserve the exact run ordinals.
    private List<string[]?>? _aliasOverrides;
    // Offscreen lazy boxes have not resolved any run style yet. Preserve the
    // ordinal cache contract without allocating a List and null slots for them.
    private List<ElementStyle?>? _runStyles;
    // Nested link styles are resolved once per measured box. Plain links never
    // allocate this cache, and immutable maps remain safely shareable by private
    // placement clones while each clone resolves against its own context.
    private List<ElementStyle[]?>? _linkSpanStyles;

    internal readonly List<string[]?>? AliasOverrides => _aliasOverrides;

    internal readonly List<ElementStyle?>? RunStyles => _runStyles;

    internal readonly List<ElementStyle[]?>? LinkSpanStyles => _linkSpanStyles;

    internal readonly int GetRunStyleCount(int runCount) => _runStyles?.Count ?? runCount;

    internal readonly ElementStyle? GetCachedRunStyle(int index, int runCount)
    {
        if ((uint)index >= (uint)runCount)
            throw new ArgumentOutOfRangeException(nameof(index));
        return _runStyles?[index];
    }

    internal void CacheRunStyle(int index, ElementStyle resolved, int runCount)
    {
        if (_runStyles is null)
        {
            var styles = new List<ElementStyle?>(runCount);
            for (int slot = 0; slot < runCount; slot++)
                styles.Add(null);
            _runStyles = styles;
        }
        _runStyles[index] = resolved;
    }

    internal List<ElementStyle[]?> GetOrCreateLinkSpanStyles(int runCount)
    {
        List<ElementStyle[]?>? cache = _linkSpanStyles;
        if (cache is null)
        {
            cache = new List<ElementStyle[]?>(runCount);
            for (int index = 0; index < runCount; index++)
                cache.Add(null);
            _linkSpanStyles = cache;
        }
        return cache;
    }

    internal readonly bool HasWellFormedAliasOverrides(int runCount) =>
        _aliasOverrides is null || _aliasOverrides.Count == runCount;

    internal readonly bool HasWellFormedLinkSpanStyles(IReadOnlyList<InlineRun> runs)
    {
        List<ElementStyle[]?>? cache = _linkSpanStyles;
        if (cache is null)
            return true;
        if (cache.Count != runs.Count)
            return false;

        for (int index = 0; index < cache.Count; index++)
        {
            if (cache[index] is not { } styles)
                continue;
            if (runs[index] is not LinkRun link ||
                link.InlineStyleMap is not { } map ||
                !map.IsValidForTextLength(link.TextLength) || styles.Length != map.Count)
                return false;
        }
        return true;
    }

    internal readonly string[] GetEffectiveAliases(int index, int runCount, IReadOnlyList<string> containerAliases)
    {
        if ((uint)index >= (uint)runCount)
            throw new ArgumentOutOfRangeException(nameof(index));

        List<string[]?>? overrides = _aliasOverrides;
        if (overrides is not null)
        {
            if (overrides.Count != runCount)
                throw new InvalidOperationException("Run alias override ordinals are inconsistent.");
            if (overrides[index] is { } aliases)
                return aliases;
        }

        return (string[])containerAliases;
    }

    internal readonly string[]? GetAliasOverride(int index, int runCount)
    {
        if ((uint)index >= (uint)runCount)
            throw new ArgumentOutOfRangeException(nameof(index));

        List<string[]?>? overrides = _aliasOverrides;
        if (overrides is null)
            return null;
        if (overrides.Count != runCount)
            throw new InvalidOperationException("Run alias override ordinals are inconsistent.");
        return overrides[index];
    }

    internal void AppendAliasOverride(int index, int runCount, string[]? aliases)
    {
        // Add the run before recording its override so the logical ordinal and
        // run count remain the same fence used by style lookup and transfers.
        if ((uint)index >= (uint)runCount || index != runCount - 1)
            throw new InvalidOperationException("Run alias overrides must be appended in run order.");

        List<string[]?>? overrides = _aliasOverrides;
        if (overrides is null)
        {
            if (aliases is null)
                return;

            overrides = new List<string[]?>(runCount);
            for (int prior = 0; prior < index; prior++)
                overrides.Add(null);
            overrides.Add(aliases);
            _aliasOverrides = overrides;
            return;
        }

        if (overrides.Count != index)
            throw new InvalidOperationException("Run alias overrides must be contiguous by ordinal.");
        overrides.Add(aliases);
    }

    internal void AppendRebuiltAliasOverride(int index, int runCount, string[]? aliases)
    {
        // DetachInlineImagesForTransfer has already removed all image runs, so
        // the retained run list is complete before its effective aliases are
        // rebuilt. Keep normal Add/clone appends strict, but let this dedicated
        // ordered rebuild fill the null ordinals preceding the current run.
        if ((uint)index >= (uint)runCount)
            throw new InvalidOperationException("Rebuilt run alias ordinals must belong to the retained runs.");

        List<string[]?>? overrides = _aliasOverrides;
        if (overrides is null)
        {
            if (aliases is null)
                return;

            overrides = new List<string[]?>(runCount);
            for (int prior = 0; prior < index; prior++)
                overrides.Add(null);
            overrides.Add(aliases);
            _aliasOverrides = overrides;
            return;
        }

        if (overrides.Count > index)
            throw new InvalidOperationException("Rebuilt run alias overrides must be appended in run order.");
        while (overrides.Count < index)
            overrides.Add(null);
        overrides.Add(aliases);
    }

    /// <summary>
    /// Appends a private placement clone's copy of one source run-style slot,
    /// creating the slot list with <paramref name="capacity"/> on first use.
    /// </summary>
    /// <param name="style">The source box's resolved style for the copied run.</param>
    /// <param name="capacity">The clone's final run count.</param>
    internal void AppendCopiedRunStyle(ElementStyle? style, int capacity)
        => (_runStyles ??= new(capacity)).Add(style);

    /// <summary>
    /// Extends any existing style slot lists with the unresolved slot of a run
    /// that was just appended to the owning box.
    /// </summary>
    internal readonly void AppendUnresolvedRunSlots()
    {
        _runStyles?.Add(null);
        _linkSpanStyles?.Add(null);
    }

    internal void Clear()
    {
        _aliasOverrides = null;
        _runStyles = null;
        _linkSpanStyles = null;
    }

    /// <summary>Copies style keys into a private array the source can never mutate.</summary>
    /// <param name="source">The keys to copy.</param>
    internal static string[] CopyKeys(IReadOnlyList<string> source)
    {
        var copy = new string[source.Count];
        for (int index = 0; index < copy.Length; index++)
            copy[index] = source[index];
        return copy;
    }

    /// <summary>
    /// Returns a run's effective aliases: the container's aliases followed by
    /// the run's own, reusing either array when the other is empty.
    /// </summary>
    /// <param name="containerAliases">The owning container's aliases.</param>
    /// <param name="runAliases">The run's own aliases.</param>
    internal static string[] CombineAliases(
        IReadOnlyList<string> containerAliases,
        IReadOnlyList<string> runAliases)
    {
        if (runAliases.Count == 0)
            return containerAliases as string[] ?? CopyKeys(containerAliases);
        if (containerAliases.Count == 0)
            return runAliases as string[] ?? CopyKeys(runAliases);

        var aliases = new string[containerAliases.Count + runAliases.Count];
        for (int n = 0; n < containerAliases.Count; n++)
            aliases[n] = containerAliases[n];
        for (int n = 0; n < runAliases.Count; n++)
            aliases[containerAliases.Count + n] = runAliases[n];
        return aliases;
    }
}

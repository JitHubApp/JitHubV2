using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using MarkdownRenderer.Controls.Internal.View;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.CodeBlocks;
using MarkdownRenderer.Layout.Internal.Focus;
using MarkdownRenderer.Layout.Internal.Placement;
using MarkdownRenderer.Layout.Internal.Snapshots;

namespace MarkdownRenderer.Controls.Internal.Embeds;

/// <summary>
/// Owns the code block copy button plans of the published layout: the plans,
/// their viewport index, which of them are realized, and the copy buttons'
/// share of the hit-test rectangles. <see cref="EmbedRealization"/> decides
/// when each step runs.
/// </summary>
internal sealed class CodeBlockActionRealization
{
    private readonly EmbedRealization _realization;
    private readonly IEmbedRealizationHost _host;
    private readonly EmbedPlanContext _context;
    private readonly EmbedHitTargets _hitTargets;
    private readonly CodeBlockCopyButtons _copyButtons;
    private readonly MarkdownViewport _viewport;

    private List<CodeBlockActionPlan> _codeBlockActionPlans = new();
    private bool _hasMeasuredCodeBlocks;
    private ViewportBandIndex? _codeBlockActionPlanIndex;
    private List<int> _activeCodeBlockActionOrdinals = new();

    internal CodeBlockActionRealization(
        EmbedRealization realization,
        IEmbedRealizationHost host,
        EmbedPlanContext context,
        EmbedHitTargets hitTargets,
        CodeBlockCopyButtons copyButtons,
        MarkdownViewport viewport)
    {
        _realization = realization;
        _host = host;
        _context = context;
        _hitTargets = hitTargets;
        _copyButtons = copyButtons;
        _viewport = viewport;
    }

    /// <summary>Code block copy button plans of the published layout, in document order.</summary>
    internal IReadOnlyList<CodeBlockActionPlan> Plans => _codeBlockActionPlans;

    /// <summary>True when plan collection measured at least one code block.</summary>
    internal bool HasMeasuredCodeBlocks => _hasMeasuredCodeBlocks;

    /// <summary>The viewport index over <see cref="Plans"/>, or null when there are none.</summary>
    internal ViewportBandIndex? Index => _codeBlockActionPlanIndex;

    internal bool Contains(CodeBlockActionPlan plan) => _codeBlockActionPlans.Contains(plan);

    /// <summary>
    /// Records that a code block was measured and collects its copy button
    /// plan when the button is enabled and has a size.
    /// </summary>
    internal void Collect(LayoutSnapshot snapshot, BlockBox topLevelRoot, CodeBlockBox codeBlock)
    {
        _hasMeasuredCodeBlocks = true;
        if (codeBlock.IsCopyButtonEnabled && codeBlock.CopyButtonBounds.Width > 0 && codeBlock.CopyButtonBounds.Height > 0)
            _codeBlockActionPlans.Add(new CodeBlockActionPlan
            {
                Box = codeBlock,
                FocusIdentity = new FocusableItem(
                    codeBlock.BlockIndex, 0, FocusableItemKind.CodeBlockCopy),
                Rect = snapshot.ToLogicalRectForTopLevelBlockUnderPaint(
                    topLevelRoot, codeBlock.CopyButtonBounds),
                Kind = CodeBlockHostedElementKind.Copy,
            });
    }

    /// <summary>Indexes the collected plans by vertical extent.</summary>
    internal void CommitIndex() =>
        _codeBlockActionPlanIndex = EmbedRealization.BuildPlanIndex(_codeBlockActionPlans);

    /// <summary>Forgets every plan and its index. Realized buttons must already be released.</summary>
    internal void Clear()
    {
        _codeBlockActionPlans.Clear();
        _hasMeasuredCodeBlocks = false;
        _codeBlockActionPlanIndex = null;
        _activeCodeBlockActionOrdinals.Clear();
    }

    /// <summary>Forgets which plans are realized, after every button was released.</summary>
    internal void ClearActiveOrdinals() => _activeCodeBlockActionOrdinals.Clear();

    /// <summary>Copies the current plans so a teardown or rebuild can work on a detached array.</summary>
    internal CodeBlockActionPlan[] CapturePlans() => _codeBlockActionPlans.ToArray();

    /// <summary>
    /// Moves realized buttons from <paramref name="oldActionPlans"/> to the
    /// new plans for the same logical action. Returns false as soon as the
    /// publication of <paramref name="snapshot"/> stops being current.
    /// </summary>
    internal bool AdoptRealizedElements(
        CodeBlockActionPlan[] oldActionPlans,
        LayoutSnapshot snapshot,
        long planGeneration,
        long hostedFocusPlanEpoch,
        long hostedFocusPlanRevision)
    {
        if (oldActionPlans.Length > 0)
        {
            var oldByBox = new Dictionary<CodeBlockBox, List<CodeBlockActionPlan>>(
                ReferenceEqualityComparer.Instance);
            foreach (CodeBlockActionPlan oldPlan in oldActionPlans)
            {
                if (oldPlan.Realized is null)
                    continue;
                if (!oldByBox.TryGetValue(oldPlan.Box, out var candidates))
                    oldByBox.Add(oldPlan.Box, candidates = []);
                candidates.Add(oldPlan);
            }

            for (int ordinal = 0; ordinal < _codeBlockActionPlans.Count; ordinal++)
            {
                if (!_host.IsCurrentPlanPublication(
                        snapshot, planGeneration, hostedFocusPlanEpoch, hostedFocusPlanRevision))
                    return false;
                CodeBlockActionPlan newPlan = _codeBlockActionPlans[ordinal];
                if (!oldByBox.TryGetValue(newPlan.Box, out var candidates))
                    continue;
                for (int index = 0; index < candidates.Count; index++)
                {
                    CodeBlockActionPlan oldPlan = candidates[index];
                    if (!newPlan.IsSameLogicalAction(oldPlan))
                        continue;

                    newPlan.AdoptRealizedFrom(oldPlan, _context);
                    if (!_host.IsCurrentPlanPublication(
                            snapshot, planGeneration, hostedFocusPlanEpoch, hostedFocusPlanRevision))
                        return false;
                    candidates.RemoveAt(index);
                    break;
                }
            }
        }

        return true;
    }

    /// <summary>Marks every plan holding a realized button as active.</summary>
    internal void MarkActiveOrdinals()
    {
        for (int index = 0; index < _codeBlockActionPlans.Count; index++)
        {
            if (_codeBlockActionPlans[index].Realized is not null)
                _activeCodeBlockActionOrdinals.Add(index);
        }
    }

    /// <summary>
    /// Releases the buttons still held by <paramref name="actionPlans"/>.
    /// The array is detached, so native callbacks cannot invalidate it.
    /// </summary>
    internal void DerealizeDetached(CodeBlockActionPlan[] actionPlans)
    {
        foreach (CodeBlockActionPlan plan in actionPlans)
        {
            if (plan.Realized is not null) plan.Derealize(_context);
        }
    }

    /// <summary>
    /// Takes over the code block action plans that a raster-candidate
    /// publication prepared off the published state.
    /// </summary>
    internal void AdoptPublication(
        List<CodeBlockActionPlan> plans,
        ViewportBandIndex? index,
        List<int> activeOrdinals,
        bool hasMeasuredCodeBlocks,
        List<(CodeBlockBox Box, Rect Rect, CodeBlockHostedElementKind Kind)> rects)
    {
        _codeBlockActionPlans = plans;
        _codeBlockActionPlanIndex = index;
        _activeCodeBlockActionOrdinals = activeOrdinals;
        _hasMeasuredCodeBlocks = hasMeasuredCodeBlocks;
        _hitTargets.ReplaceCodeActions(rects);
    }

    /// <summary>
    /// Forgets the copy button at <paramref name="ordinal"/> after a raster
    /// candidate failed to adopt its button.
    /// </summary>
    internal void Forget(int ordinal, CodeBlockBox box)
    {
        _activeCodeBlockActionOrdinals?.Remove(ordinal);
        _hitTargets.RemoveCodeAction(box);
    }

    /// <summary>Applies the current copy button labels to realized buttons.</summary>
    internal void UpdateCopyButtonLabels()
    {
        foreach (var plan in _codeBlockActionPlans)
        {
            if (plan.Realized is Button button)
                _copyButtons.SetState(button, copied: false);
        }
    }

    /// <summary>Applies the current copy button style to realized buttons.</summary>
    internal void UpdateCopyButtonStyles(Style? style)
    {
        foreach (var plan in _codeBlockActionPlans)
        {
            if (plan.Realized is Button button)
                button.Style = style;
        }
    }

    /// <summary>
    /// Counts realized copy buttons, and how many of them lie outside the
    /// viewport.
    /// </summary>
    internal void GetRealizationCounts(out int realizedCount, out int offscreenCount)
    {
        _viewport.TryGet(out double top, out double viewportHeight, out _);
        double bottom = top + viewportHeight;
        realizedCount = 0;
        offscreenCount = 0;
        foreach (int ordinal in _activeCodeBlockActionOrdinals)
        {
            CodeBlockActionPlan plan = _codeBlockActionPlans[ordinal];
            if (plan.Realized is null)
                continue;
            realizedCount++;
            if (plan.Rect.Bottom < top || plan.Rect.Top > bottom)
                offscreenCount++;
        }
    }

    /// <summary>
    /// Realizes the copy buttons that intersect the viewport, releases the
    /// ones that left it, and adds the realized buttons' hit rectangles.
    /// Returns false as soon as <paramref name="pass"/> stops being current.
    /// </summary>
    internal bool RealizeVisible(in VisibleRealizationPass pass, double top, double bottom)
    {
        ViewportBandIndex? actionIndex = pass.ActionIndex;
        ViewportRange actionRange = actionIndex?.Find(top, bottom) ?? default;
        for (int index = actionRange.Start; index < actionRange.End; index++)
        {
            int ordinal = actionIndex!.GetBlockOrdinal(index);
            CodeBlockActionPlan plan = _codeBlockActionPlans[ordinal];
            double actionTop = plan.Rect.Top;
            double actionBottom = plan.Rect.Bottom;
            // Copy buttons are XAML controls, so retaining them in an overscan
            // band creates off-screen automation peers and warm-scroll churn.
            // Realize only actions that intersect the effective viewport; the
            // detached bounded pool makes boundary crossings inexpensive.
            bool inRealize = actionBottom >= top && actionTop <= bottom;
            if (inRealize)
            {
                plan.Realize(_context);
                if (!_realization.IsCurrent(pass))
                    return false;
                if (plan.Realized is not null)
                    EmbedRealization.AddActiveOrdinal(_activeCodeBlockActionOrdinals, ordinal);
            }
        }

        for (int index = _activeCodeBlockActionOrdinals.Count - 1; index >= 0; index--)
        {
            int ordinal = _activeCodeBlockActionOrdinals[index];
            CodeBlockActionPlan plan = _codeBlockActionPlans[ordinal];
            if (plan.Rect.Bottom >= top && plan.Rect.Top <= bottom)
                continue;
            plan.Derealize(_context);
            if (!_realization.IsCurrent(pass))
                return false;
            _activeCodeBlockActionOrdinals.RemoveAt(index);
        }

        foreach (int ordinal in _activeCodeBlockActionOrdinals)
        {
            CodeBlockActionPlan plan = _codeBlockActionPlans[ordinal];
            if (plan.Realized is not null)
            {
                double left = Math.Round(plan.Rect.X);
                double t = Math.Round(plan.Rect.Y);
                double w = Math.Round(plan.Rect.X + plan.Rect.Width) - left;
                double h = Math.Round(plan.Rect.Y + plan.Rect.Height) - t;
                _hitTargets.AddCodeAction(plan.Box, new Rect(left, t, w, h), plan.Kind);
            }
        }

        return true;
    }
}

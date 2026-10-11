using System.Reflection;
using MarkdownRenderer.Controls;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

// These are call-site contracts, not WinUI integration or timing tests. The
// concurrent snapshot admission behavior has separate compiled tests.
public sealed class RealizationPlanReadLeaseWiringTests
{
    [Fact]
    public void PlanGeometryIsCollectedUnderOneNonblockingLeaseBeforeAnyNativeCallbacks()
    {
        string source = ReadControl();
        string body = Method(source, "private bool RebuildRealizationPlans(", "private static ViewportBandIndex? BuildPlanIndex(");
        int admission = body.IndexOf("if (!snapshot.TryBeginInteraction())", StringComparison.Ordinal);
        int clear = body.IndexOf("UnsubscribeAllImages();", StringComparison.Ordinal);
        int collect = body.IndexOf("CollectEmbedPlans(snapshot, candidateRoot!, candidate.Box);", StringComparison.Ordinal);
        int imageBounds = body.IndexOf(
            "Rect bounds = snapshot.ToLogicalRectForBlockIndexUnderPaint(",
            collect,
            StringComparison.Ordinal);
        int focusCapture = body.IndexOf("snapshot.TryCollectFocusableItemsUnderReadLease(", StringComparison.Ordinal);
        int revision = body.IndexOf("_appliedLazyLayoutRevision = hostedFocusPlanRevision;", StringComparison.Ordinal);
        int release = body.IndexOf("snapshot.EndInteraction();", StringComparison.Ordinal);
        int residency = body.IndexOf("PrepareImageResidency(snapshot);", StringComparison.Ordinal);
        int placement = body.IndexOf("newPlan.UpdatePlacement();", StringComparison.Ordinal);
        int finalFence = body.LastIndexOf("if (!plansBuilt ||", StringComparison.Ordinal);
        int focusPublication = body.IndexOf("_focusableItems = collectedFocusableItems;", StringComparison.Ordinal);
        Assert.True(admission >= 0 && clear > admission && collect > clear && imageBounds > collect);
        Assert.True(focusCapture > admission && clear > focusCapture);
        Assert.True(revision > imageBounds && release > revision);
        Assert.Contains("finally", body[revision..release], StringComparison.Ordinal);
        Assert.True(residency > release && placement > release);
        Assert.True(finalFence > residency && finalFence > placement && focusPublication > finalFence);
        Assert.Contains("!IsCurrentFocusablePlanAttempt(", body[finalFence..focusPublication], StringComparison.Ordinal);
        Assert.DoesNotContain("snapshot.CollectFocusableItems()", body, StringComparison.Ordinal);
        Assert.Contains("return false;", body[admission..clear], StringComparison.Ordinal);
        Assert.DoesNotContain("snapshot.GetMeasuredTopLevelBlocks()", body, StringComparison.Ordinal);
        Assert.Contains("snapshot.GetLayoutPlanCandidatesUnderPaint()", body, StringComparison.Ordinal);
    }

    [Fact]
    public void BusyPublicationDefersBeforeChangingCanvasOrScrollAndDoesNotAdvanceTheRevision()
    {
        string source = ReadControl();
        string body = Method(source, "private void ApplyLazyLayoutCommit(", "private void DeferLayoutCommit(");
        int collect = body.IndexOf("if (!RebuildRealizationPlans(", StringComparison.Ordinal);
        int defer = body.IndexOf("DeferLayoutCommit(scrollAnchor);", collect, StringComparison.Ordinal);
        int size = body.IndexOf("ApplySnapshotSize(size);", StringComparison.Ordinal);
        int scroll = body.IndexOf("scroll.ChangeView(", StringComparison.Ordinal);
        Assert.True(collect >= 0 && defer > collect && size > defer && scroll > size);
        Assert.Contains("return;", body[defer..size], StringComparison.Ordinal);
        Assert.DoesNotContain("_appliedLazyLayoutRevision =", body, StringComparison.Ordinal);
        Assert.DoesNotContain("RestoreScrollAnchor(", body, StringComparison.Ordinal);
        Assert.DoesNotContain("snapshot.CollectFocusableItems()", body, StringComparison.Ordinal);
        Assert.Contains("if (_publishingLayoutCommit)", body, StringComparison.Ordinal);
        Assert.Contains("_publishingLayoutCommit = false;", body, StringComparison.Ordinal);
    }

    [Fact]
    public void DeferredPublicationUsesValueIdentityAndTheExistingWeakRecoveryRoute()
    {
        Type control = typeof(MarkdownDocumentView).BaseType!;
        FieldInfo field = control.GetField("_pendingLayoutCommit", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.NotNull(field);
        Assert.True(field.FieldType.IsValueType);
        Assert.DoesNotContain("LayoutSnapshot", field.FieldType.ToString(), StringComparison.Ordinal);
        string source = ReadControl();
        string defer = Method(source, "private void DeferLayoutCommit(", "private void PublishPendingLayoutCommit()");
        Assert.Contains("_snapshotGeneration, scrollAnchor, _scroll?.VerticalOffset", defer, StringComparison.Ordinal);
        Assert.Contains("StartImageRelayoutRecovery();", defer, StringComparison.Ordinal);
        string retry = Method(source, "private void PublishPendingLayoutCommit()", "private void DerealizeAllEmbeds()");
        Assert.Contains("pending.Generation != _snapshotGeneration", retry, StringComparison.Ordinal);
        Assert.Contains("LazyLayoutScrollAnchorPolicy.ShouldRestore(", retry, StringComparison.Ordinal);
        Assert.Contains("ApplyLazyLayoutCommit(snapshot, restoreAnchor ? pending.Anchor : null);", retry, StringComparison.Ordinal);
        string timer = Method(source, "private void StartImageRelayoutRecovery()", "private void StopImageRelayoutRecovery(");
        Assert.Contains("control.PublishPendingLayoutCommit();", timer, StringComparison.Ordinal);
        Assert.Contains("control.ProcessQueuedLazyLayoutRealization(", timer, StringComparison.Ordinal);
        Assert.Contains("control.TryPublishLazyLayoutCompletion(control._lazyLayoutGeneration);", timer, StringComparison.Ordinal);
        Assert.Contains("control._pendingLayoutCommit = null;", timer, StringComparison.Ordinal);
        Assert.Contains("if (layoutTicket == control._lastLayoutCommitTicket)", timer, StringComparison.Ordinal);
        string idle = Method(source, "private void StopImageRelayoutRecoveryIfIdle()", "private void ProcessQueuedImageRelayout(");
        Assert.Contains("HasPendingLazyLayoutWork", idle, StringComparison.Ordinal);
        string pending = Method(source, "internal bool HasPendingLazyLayoutWork =>", "internal Task DrainRetiredSnapshotsAsync()");
        Assert.Contains("_lazyLayoutPublicationHandoff.HasPendingPublication", pending, StringComparison.Ordinal);
        Assert.Contains("_pendingLayoutCommit is not null", pending, StringComparison.Ordinal);
        Assert.Contains("++_lastLayoutCommitTicket;", defer, StringComparison.Ordinal);
        string cancel = Method(source, "private void CancelLazyLayoutRealization()", "private void ApplyLazyLayoutCommit(");
        Assert.DoesNotContain("_pendingLayoutCommit = null;", cancel, StringComparison.Ordinal);
        string release = Method(source, "private void ReleaseLoadedResources()", "private void OnScrollViewChanged(");
        Assert.Contains("_pendingLayoutCommit = null;", release, StringComparison.Ordinal);
        Assert.Contains("CancelLazyLayoutRealization();\n        _pendingLayoutCommit = null;", source.Replace("\r\n", "\n"), StringComparison.Ordinal);
        Assert.Contains("_pendingLayoutCommit is not null ||", source, StringComparison.Ordinal);
        string anchor = Method(source, "private static (int BlockIndex, double OffsetFromTop)? CaptureScrollAnchor(", "private static double? GetScrollAnchorOffset(");
        Assert.Contains("snapshot.TryCaptureScrollAnchor(", anchor, StringComparison.Ordinal);
        Assert.DoesNotContain("snapshot.Blocks", anchor, StringComparison.Ordinal);
    }

    [Fact]
    public void NativePlacementUsesCopiedGeometryAndStopsAfterReentrantOwnershipLoss()
    {
        string source = ReadControl();
        string placement = Method(source, "protected void UpdatePlacement(bool snapEdges)", "private sealed class BlockEmbedPlan");
        Assert.Contains("FrameworkElement? element = Realized;", placement, StringComparison.Ordinal);
        Assert.Contains("Rect rect = Rect;", placement, StringComparison.Ordinal);
        Assert.DoesNotContain("Box.Bounds", placement, StringComparison.Ordinal);
        Assert.DoesNotContain("Box.Margin", placement, StringComparison.Ordinal);
        int previous = -1;
        foreach (string mutation in new[] { "element.Width =", "element.Height =", "Canvas.SetLeft(element,", "Canvas.SetTop(element," })
        {
            int setter = placement.IndexOf(mutation, previous + 1, StringComparison.Ordinal);
            int fence = placement.IndexOf("if (!ReferenceEquals(Realized, element)) return;", setter, StringComparison.Ordinal);
            Assert.True(setter > previous && fence > setter);
            previous = fence;
        }
        string rebuild = Method(source, "private bool RebuildRealizationPlans(", "private static ViewportBandIndex? BuildPlanIndex(");
        Assert.DoesNotContain("foreach (var newPlan in _embedPlans)", rebuild, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var newPlan in _codeBlockActionPlans)", rebuild, StringComparison.Ordinal);
        Assert.Contains("IsCurrentRealizationSnapshot(snapshot, planGeneration)", rebuild, StringComparison.Ordinal);
        Assert.Contains("long planGeneration = _snapshotGeneration;", rebuild, StringComparison.Ordinal);
        int cleanup = rebuild.LastIndexOf("foreach (EmbedPlan oldPlan in oldPlans)", StringComparison.Ordinal);
        int publication = rebuild.LastIndexOf("Volatile.Write(ref _publishedHostedFocusPlanEpoch, hostedFocusPlanEpoch);", StringComparison.Ordinal);
        int success = rebuild.LastIndexOf("return true;", StringComparison.Ordinal);
        Assert.True(cleanup >= 0 && publication > cleanup && success > publication);
        Assert.Contains("finally", rebuild[(cleanup - 350)..cleanup], StringComparison.Ordinal);
    }

    [Fact]
    public void HostedAttachmentAndCodeActionTransferFenceReentrantNativeCallbacks()
    {
        string source = ReadControl();
        string attach = Method(source, "private void CompleteOnUiThread(", "public override void Derealize(MarkdownRendererControl owner)");
        int placement = attach.IndexOf("UpdatePlacement();", StringComparison.Ordinal);
        int fence = attach.IndexOf("if (!ReferenceEquals(Realized, element))", placement, StringComparison.Ordinal);
        int subscribe = attach.IndexOf("element.KeyDown +=", placement, StringComparison.Ordinal);
        Assert.True(placement >= 0 && fence > placement && subscribe > fence);
        Assert.Contains("ownershipEstablished && !ReferenceEquals(Realized, element)", attach, StringComparison.Ordinal);
        Assert.Contains("!owner.CanAttachHostedElement(this, pipelineGeneration)", attach, StringComparison.Ordinal);
        string adopt = Method(source, "public void AdoptRealizedFrom(CodeBlockActionPlan oldPlan,", "private void AttachHandlers(");
        int clearOld = adopt.IndexOf("oldPlan.Realized = null;", StringComparison.Ordinal);
        int ownsNew = adopt.IndexOf("Realized = fe;", StringComparison.Ordinal);
        int automation = adopt.IndexOf("AutomationProperties.SetAutomationId(", StringComparison.Ordinal);
        Assert.True(clearOld >= 0 && ownsNew > clearOld && automation > ownsNew);
        Assert.Contains("if (!ReferenceEquals(Realized, fe) || owner._isDisposed || owner._isUnloaded)", adopt, StringComparison.Ordinal);
        string retire = Method(source, "public void Derealize(MarkdownRendererControl owner)", "public void AdoptRealizedFrom(CodeBlockActionPlan oldPlan,");
        Assert.True(retire.IndexOf("Realized = null;", StringComparison.Ordinal) <
                    retire.IndexOf("owner.PreserveLogicalFocusBeforeDerealizingCodeBlockAction(fe)", StringComparison.Ordinal));
    }

    [Fact]
    public void LazyDispatchWithoutARecoveryTimerDropsItsEpochAndRequestsAFullRebuild()
    {
        string source = ReadControl();
        string queue = Method(source, "private void QueueLazyLayoutForBand(", "private void ProcessQueuedLazyLayoutRealization(");
        int startTimer = queue.IndexOf("StartImageRelayoutRecovery();", StringComparison.Ordinal);
        int unavailable = queue.IndexOf("if (!HasImageRelayoutRecoveryClock)", startTimer, StringComparison.Ordinal);
        int cancel = queue.IndexOf("CancelLazyLayoutRealization();", unavailable, StringComparison.Ordinal);
        int rebuild = queue.IndexOf("RequestRebuild();", cancel, StringComparison.Ordinal);
        Assert.True(startTimer >= 0 && unavailable > startTimer && cancel > unavailable && rebuild > cancel);
        Assert.Contains("return;", queue[rebuild..], StringComparison.Ordinal);

        int clockStart = source.IndexOf("private bool HasImageRelayoutRecoveryClock =>", StringComparison.Ordinal);
        int clockEnd = source.IndexOf(';', clockStart);
        Assert.True(clockStart >= 0 && clockEnd > clockStart);
        string recoveryClock = source[clockStart..(clockEnd + 1)];
        Assert.Contains("_imageRelayoutRecoveryTimer is not null ||", recoveryClock, StringComparison.Ordinal);
        Assert.Contains("_managedImageRelayoutRecoveryTimer is not null", recoveryClock, StringComparison.Ordinal);
    }

    [Fact]
    public void VisibleRealizationUsesPublishedValueGeometryAndFencesNativeCallbacks()
    {
        string source = ReadControl();
        string wrapper = Method(source, "internal void RealizeVisibleEmbeds()", "private void RealizeVisibleEmbedsCore()");
        Assert.Contains("_realizingVisibleEmbeds", wrapper, StringComparison.Ordinal);
        Assert.Contains("_realizeVisibleEmbedsRequested = true;", wrapper, StringComparison.Ordinal);
        Assert.Contains("_derealizingAllEmbeds", wrapper, StringComparison.Ordinal);
        Assert.Contains("for (int pass = 0; pass < 2; pass++)", wrapper, StringComparison.Ordinal);
        Assert.Contains("QueueVisibleRealizationContinuation();", wrapper, StringComparison.Ordinal);
        Assert.DoesNotContain("while (_realizeVisibleEmbedsRequested", wrapper, StringComparison.Ordinal);

        string continuation = Method(source, "private void QueueVisibleRealizationContinuation()", "private void ProcessQueuedVisibleRealizationContinuation(");
        Assert.Contains("new WeakReference<MarkdownRendererControl>(this)", continuation, StringComparison.Ordinal);
        Assert.Contains("Microsoft.UI.Dispatching.DispatcherQueuePriority.Low", continuation, StringComparison.Ordinal);
        Assert.Contains("receiver.TryGetTarget(out var control)", continuation, StringComparison.Ordinal);
        Assert.DoesNotContain("() => RealizeVisibleEmbeds()", continuation, StringComparison.Ordinal);
        string queued = Method(source, "private void ProcessQueuedVisibleRealizationContinuation(", "private void RealizeVisibleEmbedsCore()");
        Assert.Contains("ticket != _realizeVisibleEmbedsContinuationTicket", queued, StringComparison.Ordinal);
        Assert.Contains("RealizeVisibleEmbeds();", queued, StringComparison.Ordinal);

        string body = Method(source, "private void RealizeVisibleEmbedsCore()", "private bool IsCurrentVisibleRealizationPass(");
        Assert.Contains("imageIndex.GetBounds(index)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("img.Bounds", body, StringComparison.Ordinal);
        Assert.DoesNotContain("bp.Box.Bounds", body, StringComparison.Ordinal);
        Assert.DoesNotContain("bp.Box.Margin", body, StringComparison.Ordinal);
        Assert.Contains("Rect rect = bp.Rect;", body, StringComparison.Ordinal);
        Assert.Contains("Math.Round(rect.Width)", body, StringComparison.Ordinal);
        Assert.Contains("Math.Round(rect.Height)", body, StringComparison.Ordinal);

        AssertCallbackFences(body, "img.EnsureLoadingForImageActivationTrace(", "IsCurrentVisibleRealizationPass(", 1);
        AssertCallbackFences(body, "img.EnsureLoading(isVisible:", "IsCurrentVisibleRealizationPass(", 2);
        AssertCallbackFences(body, "plan.Realize(this);", "IsCurrentVisibleRealizationPass(", 2);
        AssertCallbackFences(body, "plan.Derealize(this);", "IsCurrentVisibleRealizationPass(", 2);

        string fence = Method(source, "private bool IsCurrentVisibleRealizationPass(", "#if MARKDOWN_LAYOUT_DIAGNOSTICS");
        Assert.Contains("realizationPassEpoch == _realizationPassEpoch", fence, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(imageIndex, _imagePlanIndex)", fence, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(embedIndex, _embedPlanIndex)", fence, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(actionIndex, _codeBlockActionPlanIndex)", fence, StringComparison.Ordinal);
    }

    [Fact]
    public void ScrollRealizationObservesSvgVisibilityBeforeLoadCallbacksEvenWhenPaintCullsTheImage()
    {
        // Source/call-site contract only. Managed ImageBox tests separately
        // exercise visibility, exact-plan fencing and pressure-parking state.
        string source = ReadControl();
        string body = Method(source, "private void RealizeVisibleEmbedsCore()", "private bool IsCurrentVisibleRealizationPass(");
        int range = body.IndexOf("imageIndex?.Find(imgLoadTop, imgLoadBottom)", StringComparison.Ordinal);
        int loop = body.IndexOf("for (int index = imageRange.Start; index < imageRange.End; index++)", range, StringComparison.Ordinal);
        int bounds = body.IndexOf("imageIndex.GetBounds(index)", loop, StringComparison.Ordinal);
        int classification = body.IndexOf("bool intersectsViewport = imageBottom >= top && imageTop <= bottom;", bounds, StringComparison.Ordinal);
        int fence = body.IndexOf("if (!IsCurrentVisibleRealizationPass(", classification, StringComparison.Ordinal);
        int capture = body.IndexOf("img.CaptureSvgViewportObservationToken();", fence, StringComparison.Ordinal);
        int observation = body.IndexOf("img.ObserveSvgViewportVisibility(svgObservation, intersectsViewport);", capture, StringComparison.Ordinal);
        int conditional = body.IndexOf("#if MARKDOWN_LAYOUT_DIAGNOSTICS", observation, StringComparison.Ordinal);
        int tracedLoad = body.IndexOf("img.EnsureLoadingForImageActivationTrace(", conditional, StringComparison.Ordinal);
        Assert.True(range >= 0 && loop > range && bounds > loop && classification > bounds);
        Assert.True(fence > classification && capture > fence && observation > capture);
        Assert.True(conditional > observation && tracedLoad > conditional);
        Assert.Contains("return;", body[fence..capture], StringComparison.Ordinal);
        Assert.DoesNotContain("img.Bounds", body, StringComparison.Ordinal);

        string snapshotPath = Path.Combine(Path.GetDirectoryName(FindControlPath())!, "..", "Layout", "LayoutSnapshot.cs");
        string snapshot = File.ReadAllText(snapshotPath);
        string paint = Method(snapshot, "public void Paint(CanvasDrawingSession ds, Rect viewport)", "public void PaintSelectionForeground(");
        int cull = paint.IndexOf(
            "if (placement.Bottom < viewport.Top || placement.Top > viewport.Bottom) continue;",
            StringComparison.Ordinal);
        int measured = paint.IndexOf("if (!placement.Measured) continue;", cull, StringComparison.Ordinal);
        int imagePaint = paint.IndexOf(
            "b.Paint(ds, placements.ToPhysicalViewport(n, viewport));",
            StringComparison.Ordinal);
        Assert.True(cull >= 0 && measured > cull && imagePaint > measured);
    }

    [Fact]
    public void FullEmbedTeardownOwnsDetachedPlanArraysAndRejectsReentrancy()
    {
        string source = ReadControl();
        string body = Method(source, "private void DerealizeAllEmbeds()", "private bool RebuildRealizationPlans(");
        int guard = body.IndexOf("if (_derealizingAllEmbeds)", StringComparison.Ordinal);
        int epoch = body.IndexOf("_realizationPassEpoch++;", StringComparison.Ordinal);
        int embedCopy = body.IndexOf("embedPlans = _embedPlans.ToArray();", StringComparison.Ordinal);
        int actionCopy = body.IndexOf("actionPlans = _codeBlockActionPlans.ToArray();", StringComparison.Ordinal);
        int embedRetire = body.IndexOf("foreach (EmbedPlan plan in embedPlans)", StringComparison.Ordinal);
        int actionRetire = body.IndexOf("foreach (CodeBlockActionPlan plan in actionPlans)", StringComparison.Ordinal);
        int initializedArrays = body.IndexOf("EmbedPlan[] embedPlans = Array.Empty<EmbedPlan>();", StringComparison.Ordinal);
        int tryBlock = body.IndexOf("try", initializedArrays, StringComparison.Ordinal);
        int finallyBlock = body.IndexOf("finally", actionRetire, StringComparison.Ordinal);
        Assert.True(guard >= 0 && epoch > guard && initializedArrays > epoch && tryBlock > initializedArrays);
        Assert.True(embedCopy > tryBlock && actionCopy > embedCopy && finallyBlock > actionRetire);
        Assert.True(embedRetire > actionCopy && actionRetire > embedRetire);
        Assert.DoesNotContain("foreach (var plan in _embedPlans)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var plan in _codeBlockActionPlans)", body, StringComparison.Ordinal);
        Assert.Contains("_activeEmbedOrdinals.Clear();", body, StringComparison.Ordinal);
        Assert.Contains("_activeCodeBlockActionOrdinals.Clear();", body, StringComparison.Ordinal);
    }

    [Fact]
    public void HostedFocusPlansCopyFocusableIdentityAndPublishOnlyAfterRetirement()
    {
        string source = ReadControl();
        string collect = Method(source, "private void CollectEmbedPlans(", "private void AddImagePlan(");
        Assert.Contains("FocusIdentity = new Layout.FocusableItem(", collect, StringComparison.Ordinal);
        foreach (string kind in new[]
                 {
                     "Layout.FocusableItemKind.BlockEmbed",
                     "Layout.FocusableItemKind.DeclarativeHostedElement",
                     "Layout.FocusableItemKind.InlineEmbed",
                     "Layout.FocusableItemKind.CodeBlockCopy",
                 })
        {
            Assert.Contains(kind, collect, StringComparison.Ordinal);
        }

        string rebuild = Method(source, "private bool RebuildRealizationPlans(", "private static ViewportBandIndex? BuildPlanIndex(");
        int invalidate = rebuild.IndexOf("InvalidateHostedFocusPlanPublication();", StringComparison.Ordinal);
        int planMutation = rebuild.IndexOf("_embedPlans.Clear();", StringComparison.Ordinal);
        int successfulBuild = rebuild.IndexOf("plansBuilt = true;", StringComparison.Ordinal);
        int retirement = rebuild.LastIndexOf("foreach (EmbedPlan oldPlan in oldPlans)", StringComparison.Ordinal);
        int finalGuard = rebuild.LastIndexOf("if (!plansBuilt ||", StringComparison.Ordinal);
        int finalFence = rebuild.IndexOf("!IsCurrentRealizationSnapshot(snapshot, planGeneration)", finalGuard, StringComparison.Ordinal);
        int publication = rebuild.LastIndexOf("Volatile.Write(ref _publishedHostedFocusPlanEpoch, hostedFocusPlanEpoch);", StringComparison.Ordinal);
        Assert.True(invalidate >= 0 && planMutation > invalidate);
        Assert.True(successfulBuild > planMutation && retirement > successfulBuild);
        Assert.True(finalGuard > retirement && finalFence > finalGuard && publication > finalFence);
        Assert.Contains("snapshot.LayoutRevision != hostedFocusPlanRevision", rebuild[finalFence..publication], StringComparison.Ordinal);
        Assert.Contains("if (!plansBuilt ||", rebuild, StringComparison.Ordinal);

        string teardown = Method(source, "private void DerealizeAllEmbeds()", "private bool RebuildRealizationPlans(");
        Assert.Contains("InvalidateHostedFocusPlanPublication();", teardown, StringComparison.Ordinal);
    }

    [Fact]
    public void HostedFocusReadersUsePlanIdentityAndRejectStaleNativeFocusCallbacks()
    {
        string source = ReadControl();
        Assert.DoesNotContain("FindHostedElementForFocusable", source, StringComparison.Ordinal);

        string capture = Method(source, "private HostedFocusPlanStatus TryCaptureHostedFocusTuple(", "private bool IsCurrentHostedFocusTuple(");
        Assert.Contains("snapshot.LayoutRevision", capture, StringComparison.Ordinal);
        Assert.Contains("_publishedHostedFocusPlanGeneration", capture, StringComparison.Ordinal);
        Assert.Contains("_publishedHostedFocusPlanRevision", capture, StringComparison.Ordinal);

        string current = Method(source, "private bool IsCurrentHostedFocusTuple(", "private bool IsCurrentHostedFocusRequest(");
        Assert.Contains("tuple.SnapshotGeneration == Volatile.Read(ref _snapshotGeneration)", current, StringComparison.Ordinal);
        Assert.Contains("tuple.LayoutRevision == tuple.Snapshot.LayoutRevision", current, StringComparison.Ordinal);
        Assert.Contains("tuple.PlanEpoch == Volatile.Read(ref _publishedHostedFocusPlanEpoch)", current, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(tuple.Items, _focusableItems)", current, StringComparison.Ordinal);

        string focus = Method(source, "private HostedFocusResult TryFocusHostedElement(", "private void RestoreFocusedCodeBlockActionIfNeeded(");
        Assert.Contains("HostedFocusPlanStatus.LayoutPending", focus, StringComparison.Ordinal);
        Assert.Contains("FindRealizedHostedElement(item)", focus, StringComparison.Ordinal);
        Assert.Contains("if (TryFocusHostedDescendant(element, reverse))", focus, StringComparison.Ordinal);
        Assert.Contains("if (!IsCurrentHostedFocusOperation(tuple, item))", focus, StringComparison.Ordinal);
        Assert.DoesNotContain("TryBeginInteraction", focus, StringComparison.Ordinal);
        Assert.DoesNotContain("_snapshot.Blocks", focus, StringComparison.Ordinal);

        string lookup = Method(source, "private FrameworkElement? FindRealizedHostedElement(", "private void RestoreFocusedCodeBlockActionIfNeeded(");
        Assert.Contains("foreach (EmbedPlan plan in _embedPlans)", lookup, StringComparison.Ordinal);
        Assert.Contains("foreach (CodeBlockActionPlan plan in _codeBlockActionPlans)", lookup, StringComparison.Ordinal);
        Assert.Contains("plan.FocusIdentity", lookup, StringComparison.Ordinal);
        Assert.Contains("plan.Realized", lookup, StringComparison.Ordinal);
        Assert.DoesNotContain(".Box.", lookup, StringComparison.Ordinal);

        string reverse = Method(source, "private bool TrySetFocusedItemForHostedElement(", "private bool TryGetVectorPointerState(");
        Assert.Contains("HostedFocusPlanStatus.Current", reverse, StringComparison.Ordinal);
        Assert.Contains("plan.FocusIdentity", reverse, StringComparison.Ordinal);
        Assert.Contains("FindFocusableItemIndex(tuple.Items, focusedIdentity)", reverse, StringComparison.Ordinal);
        Assert.Contains("if (focusedIndex < 0 || !IsCurrentHostedFocusTuple(tuple))", reverse, StringComparison.Ordinal);
        Assert.DoesNotContain("_snapshot.Blocks", reverse, StringComparison.Ordinal);
        Assert.DoesNotContain("GetCellBoxes", reverse, StringComparison.Ordinal);
    }

    [Fact]
    public void DeferredHostedFocusResumeAndCallerFallbackAreGenerationFenced()
    {
        string source = ReadControl();
        Type control = typeof(MarkdownDocumentView).BaseType!;
        FieldInfo pendingField = control.GetField("_pendingLazyFocus", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Type pendingType = Nullable.GetUnderlyingType(pendingField.FieldType)!;
        Assert.NotNull(pendingType.GetField("SnapshotGeneration", BindingFlags.Instance | BindingFlags.Public));

        string resume = Method(source, "private bool ResumePendingLazyFocus(", "private Layout.FocusableItem? GetCurrentFocusableIdentity(");
        Assert.Contains("pending.SnapshotGeneration != Volatile.Read(ref _snapshotGeneration)", resume, StringComparison.Ordinal);

        string commit = Method(source, "private bool CommitFocusedItem(", "private bool ResumePendingLazyFocus(");
        int focus = commit.IndexOf("_ = Focus(FocusState.Keyboard);", StringComparison.Ordinal);
        int pendingFence = commit.IndexOf("if (!IsCurrentPendingLazyFocus(pending))", focus, StringComparison.Ordinal);
        int hide = commit.IndexOf("HideFocusRing();", pendingFence, StringComparison.Ordinal);
        int scroll = commit.IndexOf("ScrollLazyFocusTargetIntoView(estimatedBand, reverse);", hide, StringComparison.Ordinal);
        Assert.True(focus >= 0 && pendingFence > focus && hide > pendingFence && scroll > hide);
        Assert.Contains("Volatile.Read(ref _snapshotGeneration)", commit, StringComparison.Ordinal);

        string defer = Method(source, "private HostedFocusResult DeferHostedFocus(", "private HostedFocusResult TryFocusHostedElement(");
        int nativeFocus = defer.IndexOf("_ = Focus(FocusState.Keyboard);", StringComparison.Ordinal);
        int staleFence = defer.IndexOf("if (!IsCurrentHostedFocusRequest(tuple, item) ||", nativeFocus, StringComparison.Ordinal);
        int deferHide = defer.IndexOf("HideFocusRing();", staleFence, StringComparison.Ordinal);
        Assert.True(nativeFocus >= 0 && staleFence > nativeFocus && deferHide > staleFence);

        string focused = Method(source, "private void FocusCurrentItem(", "private void NotifyFocusedItemAutomation(");
        int stale = focused.IndexOf("HostedFocusResult.Stale", StringComparison.Ordinal);
        int fallback = focused.IndexOf("Focus(FocusState.Keyboard);", stale, StringComparison.Ordinal);
        Assert.True(stale >= 0 && fallback > stale);
        Assert.Contains("HostedFocusResult.Deferred or HostedFocusResult.Stale", focused, StringComparison.Ordinal);
        Assert.Contains("return;", focused[stale..fallback], StringComparison.Ordinal);
        int capture = focused.IndexOf("TryCaptureHostedFocusTuple(out HostedFocusTuple fallbackTuple)", StringComparison.Ordinal);
        int rootFocus = focused.IndexOf("Focus(FocusState.Keyboard);", capture, StringComparison.Ordinal);
        int callbackFence = focused.IndexOf("if (!IsCurrentHostedFocusRequest(fallbackTuple, item))", rootFocus, StringComparison.Ordinal);
        int ring = focused.IndexOf("UpdateFocusRing();", callbackFence, StringComparison.Ordinal);
        int ringFence = focused.IndexOf("if (!IsCurrentHostedFocusRequest(fallbackTuple, item))", ring, StringComparison.Ordinal);
        int automation = focused.IndexOf("NotifyFocusedItemAutomation();", ringFence, StringComparison.Ordinal);
        Assert.True(capture >= 0 && rootFocus > capture && callbackFence > rootFocus);
        Assert.True(ring > callbackFence && ringFence > ring && automation > ringFence);
    }

    private static void AssertCallbackFences(string body, string callback, string fence, int expectedCount)
    {
        int callbackIndex = 0;
        int found = 0;
        while ((callbackIndex = body.IndexOf(callback, callbackIndex, StringComparison.Ordinal)) >= 0)
        {
            int fenceIndex = body.IndexOf(fence, callbackIndex + callback.Length, StringComparison.Ordinal);
            Assert.True(fenceIndex > callbackIndex, $"Missing current-pass fence after {callback}");
            callbackIndex += callback.Length;
            found++;
        }

        Assert.Equal(expectedCount, found);
    }

    private static string Method(string source, string start, string end)
    {
        int first = source.IndexOf(start, StringComparison.Ordinal);
        int last = source.IndexOf(end, first + start.Length, StringComparison.Ordinal);
        Assert.True(first >= 0 && last > first);
        return source[first..last];
    }

    private static string ReadControl()
        => File.ReadAllText(FindControlPath());

    private static string FindControlPath()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string path = Path.Combine(directory.FullName, "MarkdownRenderer", "MarkdownRenderer", "Controls", "MarkdownRendererControl.cs");
            if (File.Exists(path))
                return path;
        }
        throw new FileNotFoundException("Could not locate the control source for wiring checks.");
    }
}

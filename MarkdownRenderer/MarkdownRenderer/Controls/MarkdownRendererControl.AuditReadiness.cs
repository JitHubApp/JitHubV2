using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Performance;
using Windows.Foundation;

namespace MarkdownRenderer.Controls;

public partial class MarkdownRendererControl
{
    private const int MaximumAuditReadinessImageScanCandidates = 4096;
    private const int MaximumAuditReadinessImageDetails = 8;
    private const int MaximumAuditReadinessDiagnosticCharacters = 8192;

    /// <summary>
    /// Captures a bounded, read-only readiness snapshot for a timed-out
    /// production README audit. This must not realize layout, wait for a
    /// snapshot lock, paint, or include document/image source data.
    /// </summary>
    internal string CaptureAuditReadinessDiagnostic()
    {
        var builder = new StringBuilder(2048);
        bool hasDispatcherAccess = DispatcherQueue?.HasThreadAccess == true;
        AppendAuditReadiness(builder, "pid", Environment.ProcessId);
        AppendAuditReadiness(builder, "thread", Environment.CurrentManagedThreadId);
        AppendAuditReadiness(builder, "renderer", RuntimeHelpers.GetHashCode(this).ToString("X8", CultureInfo.InvariantCulture));
        AppendAuditReadiness(builder, "dispatcherAccess", hasDispatcherAccess);
        if (!hasDispatcherAccess)
        {
            AppendAuditReadiness(builder, "snapshotState", "not-read-off-dispatcher");
            AppendAuditReadiness(builder, "peerItemStatus", "<not-read-off-dispatcher>");
            return BoundAuditReadinessDiagnostic(builder);
        }

        MarkdownAutomationPeer? peer = null;
        string peerStatus = "<not-queried>";
        string peerStatusError = "none";
        try
        {
            peer = FrameworkElementAutomationPeer.FromElement(this) as MarkdownAutomationPeer;
            if (peer is null)
                peerStatus = "<no-current-peer>";
        }
        catch (Exception exception)
        {
            peerStatus = "<peer-read-failed>";
            peerStatusError = exception.GetType().Name;
        }

        LayoutSnapshot? snapshot = _snapshot;
        ImagePlanPublication? publication = Volatile.Read(ref _imagePlanPublication);
        long snapshotGeneration = Volatile.Read(ref _snapshotGeneration);
        long pipelineGeneration = Volatile.Read(ref _pipelineGeneration);
        long activePipelineGeneration = Volatile.Read(ref _activePipelineGeneration);
        bool ownedViewportMeasured = _scroll is { ViewportWidth: > 0, ViewportHeight: > 0 };
        bool externalViewportMeasured = _hasExternalViewport &&
            double.IsFinite(_externalViewport.Width) && _externalViewport.Width > 0 &&
            double.IsFinite(_externalViewport.Height) && _externalViewport.Height > 0;
        string viewportSource = _scroll is not null
            ? "owned-scroll"
            : _hasExternalViewport ? "external" : "fallback";
        bool hasViewport = TryGetViewport(out double viewportTop, out double viewportHeight, out double viewportWidth);
        bool viewportFinite = hasViewport &&
            double.IsFinite(viewportTop) && double.IsFinite(viewportHeight) && viewportHeight > 0 &&
            double.IsFinite(viewportWidth) && viewportWidth > 0 &&
            double.IsFinite(viewportTop + viewportHeight);
        Rect viewport = viewportFinite
            ? new Rect(0, viewportTop, viewportWidth, viewportHeight)
            : default;

        AppendAuditReadiness(builder, "rendererAutomationId", AutomationProperties.GetAutomationId(this));
        AppendAuditReadiness(builder, "loaded", IsLoaded);
        AppendAuditReadiness(builder, "unloaded", _isUnloaded);
        AppendAuditReadiness(builder, "disposed", _isDisposed);
        AppendAuditReadiness(builder, "xamlRoot", XamlRoot is not null);
        AppendAuditReadiness(builder, "ownsScroll", _ownsScrollViewport);
        AppendAuditReadiness(builder, "hasExternalViewport", _hasExternalViewport);
        AppendAuditReadiness(builder, "viewportSource", viewportSource);
        AppendAuditReadiness(builder, "ownedViewportMeasured", ownedViewportMeasured);
        AppendAuditReadiness(builder, "externalViewportMeasured", externalViewportMeasured);
        AppendAuditReadiness(builder, "viewportMeasured", ownedViewportMeasured || externalViewportMeasured);
        AppendAuditReadiness(builder, "viewport", viewportFinite
            ? FormatAuditReadinessRect(viewport)
            : "unavailable-or-invalid");
        AppendAuditReadiness(builder, "rawViewport", viewportFinite
            ? string.Create(CultureInfo.InvariantCulture, $"({viewportTop:R},{viewportHeight:R},{viewportWidth:R})")
            : "unavailable-or-invalid");
        AppendAuditReadiness(builder, "pipelineGeneration", pipelineGeneration);
        AppendAuditReadiness(builder, "activePipelineGeneration", activePipelineGeneration);
        AppendAuditReadiness(builder, "snapshotGeneration", snapshotGeneration);
        AppendAuditReadiness(builder, "snapshot", snapshot is null
            ? "none"
            : RuntimeHelpers.GetHashCode(snapshot).ToString("X8", CultureInfo.InvariantCulture));
        AppendAuditReadiness(builder, "publication", publication is null
            ? "none"
            : RuntimeHelpers.GetHashCode(publication).ToString("X8", CultureInfo.InvariantCulture));
        AppendAuditReadiness(builder, "peer", peer is null
            ? "none"
            : RuntimeHelpers.GetHashCode(peer).ToString("X8", CultureInfo.InvariantCulture));
        AppendAuditReadiness(builder, "peerOwnerMatches", peer is not null && ReferenceEquals(peer.OwnerControl, this));
        AppendAuditReadiness(builder, "peerItemStatus", peerStatus);
        AppendAuditReadiness(builder, "peerItemStatusError", peerStatusError);
        AppendAuditReadiness(builder, "pendingLazyWork", HasPendingLazyLayoutWork);
        AppendAuditReadiness(builder, "lazyDispatchQueued", _lazyLayoutDispatchQueued);
        AppendAuditReadiness(builder, "hasPendingLazyBand", _hasPendingLazyLayoutBand);
        AppendAuditReadiness(builder, "lazyTask", _lazyLayoutTask?.Status.ToString() ?? "none");
        AppendAuditReadiness(builder, "appliedLazyRevision", _appliedLazyLayoutRevision);
        AppendAuditReadiness(builder, "activeLazyRevision", _activeLazyLayoutRevision);
        AppendAuditReadiness(builder, "pendingCommitGeneration", _pendingLayoutCommit?.Generation.ToString(CultureInfo.InvariantCulture) ?? "none");
        AppendAuditReadiness(builder, "imageRelayoutActive", _imageRelayoutActive);
        AppendAuditReadiness(builder, "rebuildPendingOrBuilding", _rebuildCoordinator.HasPendingOrBuilding);
        AppendAuditReadiness(builder, "pipelineCanceled", _pipelineCts?.IsCancellationRequested == true);
        AppendAuditReadiness(builder, "hasVectorCapacityAttempt", _hasVectorCapacityAttempt);
        if (_hasVectorCapacityAttempt)
        {
            AppendAuditReadiness(builder, "lastVectorCapacityAttemptBand", string.Create(
                CultureInfo.InvariantCulture,
                $"({_lastVectorCapacityAttemptVisibleBand.Top:R},{_lastVectorCapacityAttemptVisibleBand.Bottom:R})"));
        }

        if (snapshot is null)
        {
            AppendAuditReadiness(builder, "snapshotState", "missing");
            AppendAuditReadiness(builder, "images", "unavailable");
            AppendAuditReadiness(builder, "scene", "unavailable");
            return BoundAuditReadinessDiagnostic(builder);
        }

        if (!snapshot.TryBeginInteraction())
        {
            AppendAuditReadiness(builder, "snapshotState", "busy-no-wait");
            AppendAuditReadiness(builder, "images", "not-read-busy");
            AppendAuditReadiness(builder, "scene", "not-read-busy");
            return BoundAuditReadinessDiagnostic(builder);
        }

        try
        {
            long layoutRevision = snapshot.LayoutRevision;
            bool snapshotCurrent = ReferenceEquals(snapshot, _snapshot) &&
                snapshotGeneration == Volatile.Read(ref _snapshotGeneration);
            bool publicationCurrent = publication is not null &&
                IsCurrentImagePlanPublication(snapshot, snapshotGeneration, publication);
            AppendAuditReadiness(builder, "snapshotState", snapshot.IsRetiringOrDisposed
                ? "retiring-or-disposed"
                : snapshot.GeometryRecoveryRequired ? "geometry-recovery-required" : "current-read-lease");
            AppendAuditReadiness(builder, "layoutRevision", layoutRevision);
            AppendAuditReadiness(builder, "lazyLayoutEnabled", snapshot.IsLazyLayoutEnabled);
            AppendAuditReadiness(builder, "snapshotStillCurrent", snapshotCurrent);
            AppendAuditReadiness(builder, "publicationStillCurrent", publicationCurrent);

            AppendAuditReadiness(builder, "images", DescribeAuditReadinessImages(
                publication,
                snapshot,
                snapshotGeneration,
                layoutRevision,
                viewport,
                viewportFinite));
            AppendAuditReadiness(builder, "scene", DescribeAuditReadinessScene(
                snapshot,
                viewport,
                viewportFinite));
        }
        catch (Exception exception)
        {
            AppendAuditReadiness(builder, "snapshotReadError", exception.GetType().Name);
        }
        finally
        {
            snapshot.EndInteraction();
        }

        AppendAuditReadiness(builder, "snapshotCurrentAfterRead",
            ReferenceEquals(snapshot, _snapshot) &&
            snapshotGeneration == Volatile.Read(ref _snapshotGeneration));
        return BoundAuditReadinessDiagnostic(builder);
    }

    private string DescribeAuditReadinessImages(
        ImagePlanPublication? publication,
        LayoutSnapshot snapshot,
        long snapshotGeneration,
        long layoutRevision,
        Rect viewport,
        bool viewportFinite)
    {
        if (publication is null)
            return "no-publication";
        if (!ReferenceEquals(publication.Snapshot, snapshot) ||
            publication.SnapshotGeneration != snapshotGeneration ||
            publication.LayoutRevision != layoutRevision)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"stale-publication(snapshot={publication.SnapshotGeneration},revision={publication.LayoutRevision},images={publication.Images.Length})");
        }

        Layout.Boxes.ImageBox[] images = publication.Images;
        Rect[] bounds = publication.ImageBounds;
        if (bounds.Length != images.Length)
            return string.Create(CultureInfo.InvariantCulture,
                $"bounds-mismatch(images={images.Length},bounds={bounds.Length})");
        if (images.Length == 0)
            return "ready-empty";

        ViewportRange range = viewportFinite && publication.Index is { } viewportIndex
            ? viewportIndex.Find(viewport.Top, viewport.Bottom)
            : new ViewportRange(0, images.Length);
        int candidateCount = Math.Max(0, range.End - range.Start);
        int scanCount = Math.Min(candidateCount, MaximumAuditReadinessImageScanCandidates);
        int loadingCount = 0;
        int detailCount = 0;
        int invalidOrdinalCount = 0;
        var details = new StringBuilder(512);
        for (int index = 0; index < scanCount; index++)
        {
            int rangeIndex = range.Start + index;
            int ordinal = publication.Index is { } imageIndex && viewportFinite
                ? imageIndex.GetBlockOrdinal(rangeIndex)
                : rangeIndex;
            if ((uint)ordinal >= (uint)images.Length)
            {
                invalidOrdinalCount++;
                continue;
            }
            Layout.Boxes.ImageBox image = images[ordinal];
            if (image.AccessibilityState != MarkdownImageAccessibilityState.Loading)
                continue;

            Rect imageBounds = bounds[ordinal];
            bool intersects = !viewportFinite ||
                (imageBounds.Width > 0 && imageBounds.Height > 0 &&
                 imageBounds.Right > viewport.Left && imageBounds.Left < viewport.Right &&
                 imageBounds.Bottom > viewport.Top && imageBounds.Top < viewport.Bottom);
            if (!intersects)
                continue;
            loadingCount++;
            if (detailCount >= MaximumAuditReadinessImageDetails)
                continue;
            if (detailCount != 0)
                details.Append('|');
            details.Append(ordinal.ToString(CultureInfo.InvariantCulture))
                .Append(':').Append(FormatAuditReadinessRect(imageBounds));
            detailCount++;
        }

        bool scanTruncated = candidateCount > scanCount;
        return string.Create(CultureInfo.InvariantCulture,
            $"current(total={images.Length},range={range.Start}..{range.End},scanned={scanCount},loading={loadingCount},invalidOrdinal={invalidOrdinalCount},visibility={(viewportFinite ? "finite-viewport" : "unknown")},truncated={scanTruncated},details={details})");
    }

    private string DescribeAuditReadinessScene(
        LayoutSnapshot snapshot,
        Rect viewport,
        bool viewportFinite)
    {
        IMarkdownPerformanceSessionInternal? session = PerformanceSession as IMarkdownPerformanceSessionInternal;
        bool registeredWork = session?.HasRegisteredDeferredSceneWork(this) == true;
        if (!viewportFinite)
            return string.Create(CultureInfo.InvariantCulture,
                $"no-finite-viewport(registeredWork={registeredWork})");

        if (snapshot.IsRetiringOrDisposed)
            return "retiring-or-disposed";
        if (snapshot.GeometryRecoveryRequired)
            return "geometry-recovery-required";

        LazyLayoutBand band = LazyLayoutBand.FromViewport(viewport.Top, viewport.Height, overscan: 0);
        bool realizationMeasured = snapshot.IsRealizationBandMeasured(band);
        bool terminalFailure = snapshot.HasFailedScenesInBandUnderPaint(band);
        bool sceneResourcesMeasured = snapshot.IsBandMeasured(band);

        string status = terminalFailure ? "failed" :
            !realizationMeasured ? "not-measured" :
            !sceneResourcesMeasured ? "resources-not-prepared-or-measurement-incomplete" :
            registeredWork ? "deferred-work-registered" : "ready";
        string sceneWork = (session?.DescribeDeferredScenesForAudit(this) ?? "no-session") +
            string.Create(CultureInfo.InvariantCulture,
                $";probeRetryPending={DeferredSceneProbeRetryPendingForAudit}");
        return string.Create(CultureInfo.InvariantCulture,
            $"{status}(realizationMeasured={realizationMeasured},sceneResourcesMeasured={sceneResourcesMeasured},registeredWork={registeredWork},failed={terminalFailure},{sceneWork})");
    }

    private static void AppendAuditReadiness(StringBuilder builder, string key, string? value)
    {
        if (builder.Length >= MaximumAuditReadinessDiagnosticCharacters)
            return;
        if (builder.Length != 0)
            builder.Append(';');
        builder.Append(key).Append('=');
        value ??= "<null>";
        foreach (char character in value)
        {
            if (builder.Length >= MaximumAuditReadinessDiagnosticCharacters)
            {
                builder.Append("<truncated>");
                return;
            }
            if (character is ';' or '\r' or '\n' || char.IsControl(character))
                builder.Append('_');
            else
                builder.Append(character);
        }
    }

    private static void AppendAuditReadiness(StringBuilder builder, string key, bool value) =>
        AppendAuditReadiness(builder, key, value ? "true" : "false");

    private static void AppendAuditReadiness(StringBuilder builder, string key, int value) =>
        AppendAuditReadiness(builder, key, value.ToString(CultureInfo.InvariantCulture));

    private static void AppendAuditReadiness(StringBuilder builder, string key, long value) =>
        AppendAuditReadiness(builder, key, value.ToString(CultureInfo.InvariantCulture));

    private static string FormatAuditReadinessRect(Rect rect) => string.Create(
        CultureInfo.InvariantCulture,
        $"({rect.X:R},{rect.Y:R},{rect.Width:R},{rect.Height:R})");

    private static string BoundAuditReadinessDiagnostic(StringBuilder builder)
    {
        if (builder.Length <= MaximumAuditReadinessDiagnosticCharacters)
            return builder.ToString();
        return builder.ToString(0, MaximumAuditReadinessDiagnosticCharacters);
    }
}

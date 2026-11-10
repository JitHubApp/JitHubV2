using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Controls.Internal.Pipeline;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout.Internal.LazyLayout;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Performance.Internal.ProgressiveScenes;

namespace MarkdownRenderer.Performance.Internal.Session;

/// <summary>
/// Keeps each control's progressive scene plan, answers deferred-scene
/// lookups against it, and retires replaced plans outside its lock. Every
/// retirement is tracked so session shutdown can drain it.
/// </summary>
internal sealed class MarkdownDeferredScenePlanRegistry
{
    private readonly IMarkdownPerformanceSessionInternal _session;
    private readonly MarkdownPerformanceSessionLifetime _lifetime;
    private readonly object _scenePlansGate = new();
    private readonly SnapshotRetirementTracker _scenePlanRetirements = new();
    private readonly Dictionary<object, MarkdownProgressiveScenePlan> _scenePlans =
        new(ReferenceEqualityComparer.Instance);
    private readonly ConditionalWeakTable<object, RetiredDeferredSceneRegistration> _retiredSceneRegistrations = new();

    internal MarkdownDeferredScenePlanRegistry(
        IMarkdownPerformanceSessionInternal session,
        MarkdownPerformanceSessionLifetime lifetime)
    {
        _session = session;
        _lifetime = lifetime;
    }

    /// <summary>Returns the plan registered for <paramref name="documentOwner"/>, if any.</summary>
    internal bool TryGetPlan(
        object documentOwner,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out MarkdownProgressiveScenePlan? plan)
    {
        lock (_scenePlansGate)
            return _scenePlans.TryGetValue(documentOwner, out plan);
    }

    internal async ValueTask PrepareAsync(
        MarkdownRenderer.Document.MarkdownDocument document,
        object documentOwner,
        CancellationToken cancellationToken)
    {
        MarkdownProgressiveScenePlan? current;
        lock (_scenePlansGate)
        {
            _scenePlans.TryGetValue(documentOwner, out current);
            if (current?.IsFor(document) == true)
                return;
        }

        MarkdownProgressiveScenePlan? candidate = _lifetime.IsDisposed || cancellationToken.IsCancellationRequested
            ? null
            : await MarkdownProgressiveScenePlan.TryCreateAsync(
                document,
                _session,
                documentOwner,
                cancellationToken).ConfigureAwait(false);
        MarkdownProgressiveScenePlan? retired = null;
        ScenePlanRetirementReservation? retirementReservation = null;
        lock (_scenePlansGate)
        {
            if (_lifetime.IsDisposed || cancellationToken.IsCancellationRequested)
            {
                retired = candidate;
            }
            else if (_scenePlans.TryGetValue(documentOwner, out current) && current.IsFor(document))
            {
                retired = candidate;
            }
            else
            {
                if (current is not null)
                    retired = current;
                if (candidate is null)
                    _scenePlans.Remove(documentOwner);
                else
                    _scenePlans[documentOwner] = candidate;
            }

            // Publish the retirement barrier before dropping the registration
            // lock. DisposeAsync may otherwise detach this plan, run its final
            // drain, and complete before this thread records the cleanup task.
            retirementReservation = ReserveScenePlanRetirementNoLock(retired);
        }

        await StartScenePlanRetirement(
            retirementReservation,
            static plan => plan.Retire()).ConfigureAwait(false);
    }

    internal bool TryGetResult(
        object documentOwner,
        string marker,
        out MarkdownContentFragment? result)
    {
        MarkdownProgressiveScenePlan? plan;
        lock (_scenePlansGate)
            _scenePlans.TryGetValue(documentOwner, out plan);
        if (plan is not null && plan.TryGetResult(marker, out result))
            return true;
        result = null;
        return false;
    }

    internal bool TryGetResult(
        object documentOwner, string marker,
        out MarkdownContentFragment? result, out object? projectionIdentity)
    {
        lock (_scenePlansGate)
        {
            if (_scenePlans.TryGetValue(documentOwner, out MarkdownProgressiveScenePlan? plan))
                return plan.TryGetResult(marker, out result, out projectionIdentity);
        }
        result = null;
        projectionIdentity = null;
        return false;
    }

    internal bool HasRegisteredWork(object documentOwner)
    {
        lock (_scenePlansGate)
        {
            if (_scenePlans.TryGetValue(documentOwner, out MarkdownProgressiveScenePlan? plan))
                return plan.HasRegisteredWork;
            if (_retiredSceneRegistrations.TryGetValue(
                    documentOwner,
                    out RetiredDeferredSceneRegistration? retired))
                return retired.HasRegisteredWork;
        }
        return false;
    }

    internal bool IsRegisteredMarker(
        object documentOwner,
        string marker)
    {
        lock (_scenePlansGate)
        {
            if (_scenePlans.TryGetValue(documentOwner, out MarkdownProgressiveScenePlan? plan))
                return plan.IsRegisteredMarker(marker);
            if (_retiredSceneRegistrations.TryGetValue(
                    documentOwner,
                    out RetiredDeferredSceneRegistration? retired))
                return retired.Contains(marker);
        }
        return false;
    }

    internal Task<bool> PrepareBandAsync(
        object documentOwner,
        LayoutSnapshot snapshot,
        LazyLayoutBand band,
        CancellationToken cancellationToken)
    {
        MarkdownProgressiveScenePlan? plan;
        lock (_scenePlansGate)
            _scenePlans.TryGetValue(documentOwner, out plan);
        return plan is not null && !_lifetime.IsDisposed
            ? Task.Run(() => plan.RebindBandAsync(snapshot, band, cancellationToken), CancellationToken.None)
            : Task.FromResult(false);
    }

    internal IReadOnlyList<MarkdownDiagnostic> GetDiagnostics(MarkdownDocument document)
    {
        MarkdownProgressiveScenePlan[] plans;
        lock (_scenePlansGate)
            plans = _scenePlans.Values.Where(plan => plan.IsFor(document)).ToArray();
        if (plans.Length == 0)
            return Array.Empty<MarkdownDiagnostic>();
        if (plans.Length == 1)
            return plans[0].Diagnostics;

        var diagnostics = new HashSet<MarkdownDiagnostic>();
        foreach (MarkdownProgressiveScenePlan plan in plans)
            foreach (MarkdownDiagnostic diagnostic in plan.Diagnostics)
                diagnostics.Add(diagnostic);
        if (diagnostics.Count == 0)
            return Array.Empty<MarkdownDiagnostic>();

        List<MarkdownDiagnostic> ordered = diagnostics.ToList();
        ordered.Sort(static (left, right) =>
        {
            int comparison = left.SourceSpan.Start.CompareTo(right.SourceSpan.Start);
            return comparison != 0
                ? comparison
                : string.CompareOrdinal(left.Code, right.Code);
        });
        return Array.AsReadOnly(ordered.ToArray());
    }

    internal Task AdvanceGeneration(object documentOwner)
    {
        MarkdownProgressiveScenePlan? plan;
        ScenePlanRetirementReservation? reservation;
        lock (_scenePlansGate)
        {
            _scenePlans.TryGetValue(documentOwner, out plan);
            reservation = ReserveScenePlanRetirementNoLock(plan);
        }

        return StartScenePlanRetirement(
            reservation,
            static current => current.AdvanceGeneration());
    }

    internal Task Release(object documentOwner)
    {
        MarkdownProgressiveScenePlan? plan;
        ScenePlanRetirementReservation? reservation;
        lock (_scenePlansGate)
        {
            _retiredSceneRegistrations.Remove(documentOwner);
            _scenePlans.Remove(documentOwner, out plan);
            reservation = ReserveScenePlanRetirementNoLock(plan);
        }
        return StartScenePlanRetirement(reservation, static current => current.Retire());
    }

    internal bool Schedule(object documentOwner)
    {
        MarkdownProgressiveScenePlan? plan;
        lock (_scenePlansGate)
            _scenePlans.TryGetValue(documentOwner, out plan);
        return plan is not null && plan.ScheduleVisible(documentOwner);
    }

    internal void ReleaseAll()
    {
        ScenePlanRetirementReservation[] reservations;
        lock (_scenePlansGate)
        {
            var pending = new List<ScenePlanRetirementReservation>(_scenePlans.Count);
            foreach (KeyValuePair<object, MarkdownProgressiveScenePlan> entry in _scenePlans)
            {
                string[] markers = entry.Value.GetRegisteredMarkerSnapshot();
                if (markers.Length != 0)
                {
                    _retiredSceneRegistrations.AddOrUpdate(
                        entry.Key,
                        new RetiredDeferredSceneRegistration(markers));
                }

                ScenePlanRetirementReservation? reservation =
                    ReserveScenePlanRetirementNoLock(entry.Value);
                if (reservation is not null)
                    pending.Add(reservation);
            }
            _scenePlans.Clear();
            reservations = pending.ToArray();
        }

        foreach (ScenePlanRetirementReservation reservation in reservations)
            _ = StartScenePlanRetirement(reservation, static plan => plan.Retire());
    }

    /// <summary>Completes after every plan retirement tracked so far has finished.</summary>
    internal Task DrainRetirementsAsync() => _scenePlanRetirements.DrainAsync();

    private ScenePlanRetirementReservation? ReserveScenePlanRetirementNoLock(
        MarkdownProgressiveScenePlan? plan)
    {
        if (plan is null)
            return null;

        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _scenePlanRetirements.Track(completion.Task);
        return new ScenePlanRetirementReservation(plan, completion);
    }

    private static Task StartScenePlanRetirement(
        ScenePlanRetirementReservation? reservation,
        Func<MarkdownProgressiveScenePlan, Task> startRetirement)
    {
        if (reservation is null)
            return Task.CompletedTask;

        Task retirement;
        try
        {
            // Plan cancellation/disposal can take locks and invoke callback
            // completion paths, so never run it while holding _scenePlansGate.
            retirement = startRetirement(reservation.Plan);
        }
        catch (Exception exception)
        {
            CompleteScenePlanRetirementFailure(reservation.Completion, exception);
            return reservation.Completion.Task;
        }

        _ = CompleteScenePlanRetirementReservationAsync(retirement, reservation.Completion);
        return reservation.Completion.Task;
    }

    private static async Task CompleteScenePlanRetirementReservationAsync(
        Task retirement,
        TaskCompletionSource<bool> completion)
    {
        try
        {
            await retirement.ConfigureAwait(false);
            completion.TrySetResult(true);
        }
        catch (Exception exception)
        {
            CompleteScenePlanRetirementFailure(completion, exception);
        }
    }

    private static void CompleteScenePlanRetirementFailure(
        TaskCompletionSource<bool> completion,
        Exception exception)
    {
        if (exception is OperationCanceledException canceled)
            completion.TrySetCanceled(canceled.CancellationToken);
        else
            completion.TrySetException(exception);
    }
}

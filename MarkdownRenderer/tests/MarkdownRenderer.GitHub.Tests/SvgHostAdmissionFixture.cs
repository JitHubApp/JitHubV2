using System.Collections;
using System.Reflection;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using Microsoft.UI.Xaml;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Creates and initializes deferred SVG host owners and bare image boxes for
/// the SVG host admission tests, and reads their pending entries, owner
/// counts and cancellation state.
/// </summary>
internal static class SvgHostAdmissionFixture
{
    internal static void InitializeDeferredOwner(SvgHostLoadDeferral owner, MarkdownSvgRenderDemand demand, object attempt)
    {
        TestSvgHostDeferralHost.Of(owner).LoadDemand = demand;
        TestSvgHostDeferralHost.Of(owner).LoadDemandGeneration = 1;
        SetField(owner, "_svgHostDeferred", 1);
        SetField(owner, "_svgHostDeferredRegistrationAttempt", attempt);
    }

    internal static void InitializeSourceDeferredOwner(
        SvgHostLoadDeferral owner,
        MarkdownSvgRenderDemand demand,
        object attempt,
        IMarkdownSvgAdmission admission,
        long requiredBytes)
    {
        InitializeDeferredOwner(owner, demand, attempt);
        SetField(owner, "_svgHostDeferredDenialIdentity", attempt);
        SetField(owner, "_svgHostDeferredAdmission", new WeakReference<IMarkdownSvgAdmission>(admission));
        SetField(owner, "_svgHostDeferredDemandVersion", demand.Version);
        SetField(owner, "_svgHostDeferredVisualDemandEpoch", 0L);
        SetField(owner, "_svgHostDeferredCapacityVersion", admission.CapacityVersion);
        SetField(owner, "_svgHostDeferredOwnerVersion", 0L);
        SetField(owner, "_svgHostDeferredRequiredSourceBytes", requiredBytes);
        SetField(owner, "_svgHostDeferredRequiresOwnerSlot", false);
        SetField(owner, "_svgHostDeferredCapacityReady", false);
    }

    internal static SvgHostPendingEntry PendingEntryOf(
        SvgHostPendingWaiter waiter) =>
        Field<SvgHostPendingEntry>(waiter, "_entry");

    internal static bool IsCancellationDisposed(CancellationTokenSource cancellation)
    {
        try
        {
            _ = cancellation.Token;
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    // The box never loads here, so it needs no device, theme or dispatcher.
    internal static ImageBox NewBox(MarkdownRenderResources renderResources, string url, string alt) => new(
        new MarkdownLayoutContext(null!, null!, null!, null!, FlowDirection.LeftToRight) { RenderResources = renderResources },
        renderResources.Images, url, alt);

    internal static int OwnerCount(SvgHostAdmissionDomain domain) =>
        Field<ICollection>(domain.Owners, "_owners").Count;

    internal static int ReservedCount(SvgHostAdmissionDomain domain) =>
        Field<int>(domain.Owners, "_reservedOwnerSlots");

    internal static T Field<T>(object owner, string name) =>
        (T)(owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(owner)
            ?? throw new InvalidOperationException($"Missing registry field {name}."));

    internal static object? NullableField(object owner, string name) =>
        owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner);

    internal static void SetField(object owner, string name, object? value) =>
        owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, value);
}

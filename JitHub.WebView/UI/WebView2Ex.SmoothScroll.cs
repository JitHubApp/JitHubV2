// Anything in this file is NOT from the Microsoft.UI.Xaml repository
#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Windows.UI.Composition.Interactions;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml;
using Microsoft.Web.WebView2.Core;
using WebView2Ex.Natives;

namespace WebView2Ex.UI;

partial class WebView2Ex
{
    ElementInteractionTracker ElementInteractionTracker;

    [MemberNotNull(nameof(ElementInteractionTracker))]
    void SetupSmoothScroll()
    {
        ElementInteractionTracker = new(this);
        ElementInteractionTracker.ValuesChangedEvent += ElementInteractionTracker_ValuesChanged;
        PrevPosition = ElementInteractionTracker.ScrollPresenterVisualInteractionSource.Position;
    }
    Vector3 PrevPosition;
    void ElementInteractionTracker_ValuesChanged(InteractionTrackerValuesChangedArgs obj)
    {
        if (CoreWebView2 is null) return;
        var delta = obj.Position - PrevPosition;
        PrevPosition = obj.Position;
        // Advantage: This method should work anywhere
        // Disadvantage: Cannot scroll both horizontally and vertically at the same time
        var Controller = WebView2Runtime?.CompositionController;
        if (Controller is null) return;

        CoreWebView2MouseEventVirtualKeys keys = CoreWebView2MouseEventVirtualKeys.None;
        // Unlike normal pointer event, we do not get these values saying if we press these keys ourselves.
        // Therefore, we will use P/Invoke to find it.
        if (User32.IsShiftDown) keys |= CoreWebView2MouseEventVirtualKeys.Shift;
        if (User32.IsControlDown) keys |= CoreWebView2MouseEventVirtualKeys.Control;
        if (Math.Abs(delta.X) > Math.Abs(delta.Y))
            Controller.SendMouseInput(CoreWebView2MouseEventKind.HorizontalWheel,
                keys,
                (uint)delta.X,
                LatestMouseEvPosition
            );
        else
            Controller.SendMouseInput(CoreWebView2MouseEventKind.Wheel,
                keys,
                (uint)-delta.Y,
                LatestMouseEvPosition
            );
        // Advantage: Works with both horizontal/vertical scroll at the same time
        // Disadvantage: This method does not work with VSCode
        //await CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", @$"
        //{{
        //    ""type"": ""mouseWheel"",
        //    ""x"": {LatestMouseEvPosition.X},
        //    ""y"": {LatestMouseEvPosition.Y},
        //    ""deltaX"": {0},
        //    ""deltaY"": {delta.Y}
        //}}");
        //await CoreWebView2.CallDevToolsProtocolMethodAsync("Emulation.setPageScaleFactor", @$"
        //{{
        //    ""pageScaleFactor"": {obj.Scale}
        //}}");
    }
}
class ElementInteractionTracker : IInteractionTrackerOwner
{
    public InteractionTracker InteractionTracker { get; }
    public VisualInteractionSource ScrollPresenterVisualInteractionSource { get; }
    public ElementInteractionTracker(UIElement element)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        InteractionTracker = InteractionTracker.CreateWithOwner(visual.Compositor, this);
        InteractionTracker.MinPosition = new System.Numerics.Vector3(-1000, -1000, -1000);
        InteractionTracker.MaxPosition = new System.Numerics.Vector3(1000, 1000, 1000);
        InteractionTracker.MinScale = 0.5f;
        InteractionTracker.MaxScale = 5f;

        InteractionTracker.InteractionSources.Add(
            ScrollPresenterVisualInteractionSource = VisualInteractionSource.Create(visual)
        );
        ScrollPresenterVisualInteractionSource.IsPositionXRailsEnabled =
            ScrollPresenterVisualInteractionSource.IsPositionYRailsEnabled = true;


        ScrollPresenterVisualInteractionSource.PointerWheelConfig.PositionXSourceMode =
            ScrollPresenterVisualInteractionSource.PointerWheelConfig.PositionYSourceMode
            = InteractionSourceRedirectionMode.Enabled;

        ScrollPresenterVisualInteractionSource.PositionXChainingMode =
            ScrollPresenterVisualInteractionSource.ScaleChainingMode =
            InteractionChainingMode.Auto;

        ScrollPresenterVisualInteractionSource.PositionXSourceMode =
            ScrollPresenterVisualInteractionSource.PositionYSourceMode =
            ScrollPresenterVisualInteractionSource.ScaleSourceMode =
            InteractionSourceMode.EnabledWithInertia;

    }
    public void CustomAnimationStateEntered(InteractionTracker sender, InteractionTrackerCustomAnimationStateEnteredArgs args)
    {

    }

    public void IdleStateEntered(InteractionTracker sender, InteractionTrackerIdleStateEnteredArgs args)
    {

    }

    public void InertiaStateEntered(InteractionTracker sender, InteractionTrackerInertiaStateEnteredArgs args)
    {

    }

    public void InteractingStateEntered(InteractionTracker sender, InteractionTrackerInteractingStateEnteredArgs args)
    {

    }

    public void RequestIgnored(InteractionTracker sender, InteractionTrackerRequestIgnoredArgs args)
    {

    }
    Vector3 Vec = new Vector3(1000, 1000, 1000);
    public void ValuesChanged(InteractionTracker sender, InteractionTrackerValuesChangedArgs args)
    {
        InteractionTracker.MinPosition = args.Position - Vec;
        InteractionTracker.MaxPosition = args.Position + Vec;
        ValuesChangedEvent?.Invoke(args);
    }
    public event Action<InteractionTrackerValuesChangedArgs>? ValuesChangedEvent;
}
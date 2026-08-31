using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

namespace JitHub.WinUI.Views.Controls.App;

internal sealed partial class AppSvgViewportAutomationPeer : FrameworkElementAutomationPeer, ITransformProvider2
{
    private readonly AppSvgViewport _owner;

    public AppSvgViewportAutomationPeer(AppSvgViewport owner) : base(owner)
    {
        _owner = owner;
    }

    protected override string GetClassNameCore() => nameof(AppSvgViewport);

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;

    protected override string GetItemStatusCore() => _owner.RenderStatus;

    protected override bool IsControlElementCore() => true;

    protected override bool IsContentElementCore() => true;

    protected override object GetPatternCore(PatternInterface patternInterface) => patternInterface switch
    {
        PatternInterface.Transform or PatternInterface.Transform2 => this,
        _ => base.GetPatternCore(patternInterface),
    };

    bool ITransformProvider.CanMove => false;

    bool ITransformProvider.CanResize => false;

    bool ITransformProvider.CanRotate => false;

    void ITransformProvider.Move(double x, double y)
    {
    }

    void ITransformProvider.Resize(double width, double height)
    {
    }

    void ITransformProvider.Rotate(double degrees)
    {
    }

    bool ITransformProvider2.CanZoom => _owner.CanZoom;

    double ITransformProvider2.MaxZoom => _owner.MaximumZoomPercent;

    double ITransformProvider2.MinZoom => _owner.MinimumZoomPercent;

    double ITransformProvider2.ZoomLevel => _owner.ZoomPercent;

    void ITransformProvider2.Zoom(double zoom) => _owner.ZoomToPercent(zoom);

    void ITransformProvider2.ZoomByUnit(ZoomUnit zoomUnit) => _owner.ZoomByUnit(zoomUnit);

    internal void RaiseZoomLevelChanged(double previous, double current) =>
        RaisePropertyChangedEvent(TransformPattern2Identifiers.ZoomLevelProperty, previous, current);
}

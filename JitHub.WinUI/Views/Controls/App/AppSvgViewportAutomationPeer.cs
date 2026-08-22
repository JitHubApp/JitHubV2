using Microsoft.UI.Xaml.Automation.Peers;

namespace JitHub.WinUI.Views.Controls.App;

internal sealed partial class AppSvgViewportAutomationPeer : FrameworkElementAutomationPeer
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
}

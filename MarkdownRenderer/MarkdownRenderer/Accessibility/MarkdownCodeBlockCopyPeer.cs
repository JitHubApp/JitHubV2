using System.Collections.Generic;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Layout.Boxes;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

namespace MarkdownRenderer.Accessibility;

/// <summary>
/// Stable copy-command peer for a code block. It remains in the semantic tree
/// when the viewport-owned XAML button is unrealized.
/// </summary>
internal sealed partial class MarkdownCodeBlockCopyPeer : FrameworkElementAutomationPeer, IInvokeProvider
{
    private readonly MarkdownRendererControl _owner;
    private readonly MarkdownAutomationPeer _root;
    private readonly CodeBlockBox _box;

    internal MarkdownCodeBlockCopyPeer(
        MarkdownRendererControl owner,
        MarkdownAutomationPeer root,
        CodeBlockBox box)
        : base(owner)
    {
        _owner = owner;
        _root = root;
        _box = box;
    }

    protected override string GetClassNameCore() => "MarkdownCodeCopyButton";

    protected override AutomationControlType GetAutomationControlTypeCore() =>
        AutomationControlType.Button;

    protected override string GetNameCore() => _owner.ResolvedCodeBlockCopyButtonLabel;

    protected override string GetAutomationIdCore() =>
        MarkdownAutomationIdentity.ForCodeCopy(_box);

    protected override int GetCultureCore() => _owner.AutomationCultureLcid;

    protected override bool IsControlElementCore() => true;
    protected override bool IsContentElementCore() => true;
    protected override bool IsKeyboardFocusableCore() => false;

    protected override IList<AutomationPeer> GetChildrenCore() =>
        System.Array.Empty<AutomationPeer>();

    protected override object GetPatternCore(PatternInterface patternInterface) =>
        patternInterface == PatternInterface.Invoke
            ? this
            : base.GetPatternCore(patternInterface);

    protected override Windows.Foundation.Rect GetBoundingRectangleCore()
    {
        Windows.Foundation.Rect bounds = _box.CopyButtonBounds;
        return bounds.Width > 0 && bounds.Height > 0
            ? _root.GetScreenRectForDocumentRect(bounds)
            : _root.GetScreenRectForDocumentRect(_box.Bounds);
    }

    protected override bool IsOffscreenCore() =>
        _root.IsScreenRectOffscreen(GetBoundingRectangleCore());

    public void Invoke() => _owner.CopyCodeBlockFromAutomation(_box);
}

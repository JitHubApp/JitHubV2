using System;
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
    private readonly WeakReference<CodeBlockBox> _boxReference;
    private readonly MarkdownAutomationPeerLifetime _lifetime;

    internal MarkdownCodeBlockCopyPeer(
        MarkdownRendererControl owner,
        MarkdownAutomationPeer root,
        CodeBlockBox box,
        MarkdownAutomationPeerLifetime lifetime)
        : base(owner)
    {
        _owner = owner;
        _root = root;
        _boxReference = new(box);
        _lifetime = lifetime;
    }

    protected override string GetClassNameCore()
    {
        EnsureCurrentDocument();
        return "MarkdownCodeCopyButton";
    }

    protected override AutomationControlType GetAutomationControlTypeCore()
    {
        EnsureCurrentDocument();
        return AutomationControlType.Button;
    }

    protected override string GetNameCore()
    {
        EnsureCurrentDocument();
        return _owner.ResolvedCodeBlockCopyButtonLabel;
    }

    protected override string GetAutomationIdCore()
    {
        EnsureCurrentDocument();
        return MarkdownAutomationIdentity.ForCodeCopy(_box);
    }

    protected override int GetCultureCore()
    {
        EnsureCurrentDocument();
        return _owner.AutomationCultureLcid;
    }

    protected override bool IsControlElementCore()
    {
        EnsureCurrentDocument();
        return true;
    }

    protected override bool IsContentElementCore()
    {
        EnsureCurrentDocument();
        return true;
    }

    protected override bool IsKeyboardFocusableCore()
    {
        EnsureCurrentDocument();
        return false;
    }

    protected override IList<AutomationPeer> GetChildrenCore()
    {
        EnsureCurrentDocument();
        return System.Array.Empty<AutomationPeer>();
    }

    protected override object GetPatternCore(PatternInterface patternInterface)
    {
        EnsureCurrentDocument();
        return patternInterface == PatternInterface.Invoke
            ? this
            : base.GetPatternCore(patternInterface);
    }

    protected override Windows.Foundation.Rect GetBoundingRectangleCore()
    {
        EnsureCurrentDocument();
        CodeBlockBox box = _box;
        MarkdownSemanticDocument document = _lifetime.GetCurrentSemanticDocument();
        if (!_root.TryReadLayoutGeometry(
                document,
                _lifetime.DocumentGeneration,
                box,
                static (snapshot, currentBox) =>
                {
                    Windows.Foundation.Rect copyBounds = currentBox.CopyButtonBounds;
                    Windows.Foundation.Rect bounds = copyBounds.Width > 0 && copyBounds.Height > 0
                        ? copyBounds
                        : currentBox.Bounds;
                    return snapshot.ToLogicalRectForBlockIndexUnderPaint(
                        currentBox.BlockIndex, bounds);
                },
                out Windows.Foundation.Rect bounds))
        {
            return default;
        }

        return _root.GetScreenRectForDocumentRect(bounds);
    }

    protected override bool IsOffscreenCore()
    {
        EnsureCurrentDocument();
        return _root.IsScreenRectOffscreen(GetBoundingRectangleCore());
    }

    public void Invoke()
    {
        EnsureCurrentDocument();
        _owner.CopyCodeBlockFromAutomation(_box);
    }

    private void EnsureCurrentDocument() => _lifetime.EnsureCurrent();

    private CodeBlockBox _box => MarkdownAutomationPeerLifetime.GetPayloadOrThrow(_boxReference);
}

using System;
using JitHub.WinUI.Helpers;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Text;

namespace JitHub.WinUI.Views.Controls.CodeViewer;

internal sealed partial class CodeEditorAutomationPeer : FrameworkElementAutomationPeer,
    ITextProvider,
    ITextProvider2,
    ITextEditProvider,
    IValueProvider
{
    private readonly CodeEditorControl _owner;

    public CodeEditorAutomationPeer(CodeEditorControl owner) : base(owner)
    {
        _owner = owner;
    }

    protected override string GetClassNameCore() => nameof(CodeEditorControl);

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Edit;

    protected override string GetLocalizedControlTypeCore() =>
        LocalizedResourceText.GetString("RepoCode/EditorControlType", "code editor");

    protected override bool IsKeyboardFocusableCore() => true;

    protected override bool IsPasswordCore() => false;

    protected override bool HasKeyboardFocusCore() => _owner.HasNativeKeyboardFocus;

    protected override void SetFocusCore() => _owner.FocusNativeEditor();

    protected override object GetPatternCore(PatternInterface patternInterface)
    {
        if (patternInterface is PatternInterface.Text or
            PatternInterface.Text2 or
            PatternInterface.TextEdit or
            PatternInterface.Value)
        {
            return this;
        }

        return base.GetPatternCore(patternInterface);
    }

    public ITextRangeProvider DocumentRange => NativeTextProvider.DocumentRange;

    public SupportedTextSelection SupportedTextSelection => NativeTextProvider.SupportedTextSelection;

    public bool IsReadOnly => NativeValueProvider?.IsReadOnly ?? _owner.IsReadOnlyEditor;

    public string Value => NativeValueProvider?.Value ?? _owner.Text ?? string.Empty;

    public ITextRangeProvider[] GetSelection() => NativeTextProvider.GetSelection();

    public ITextRangeProvider[] GetVisibleRanges() => NativeTextProvider.GetVisibleRanges();

    public ITextRangeProvider RangeFromChild(IRawElementProviderSimple childElement) =>
        NativeTextProvider.RangeFromChild(childElement) ?? DocumentRange;

    public ITextRangeProvider RangeFromPoint(Windows.Foundation.Point screenLocation) =>
        NativeTextProvider.RangeFromPoint(screenLocation);

    public ITextRangeProvider RangeFromAnnotation(IRawElementProviderSimple annotationElement)
    {
        try
        {
            return NativeTextProvider2.RangeFromAnnotation(annotationElement);
        }
        catch (NotImplementedException)
        {
            return GetCaretRange(out _);
        }
    }

    public ITextRangeProvider GetCaretRange(out bool isActive) =>
        NativeTextProvider2.GetCaretRange(out isActive);

    public ITextRangeProvider GetActiveComposition() => GetCaretRange(out _);

    public ITextRangeProvider GetConversionTarget() => GetCaretRange(out _);

    public void SetValue(string value)
    {
        if (NativeValueProvider is { } provider)
        {
            provider.SetValue(value);
        }
    }

    private ITextProvider NativeTextProvider =>
        GetNativePattern<ITextProvider>(PatternInterface.Text) ??
        throw new InvalidOperationException("The native code editor does not expose TextPattern.");

    private ITextProvider2 NativeTextProvider2 =>
        GetNativePattern<ITextProvider2>(PatternInterface.Text2) ??
        throw new InvalidOperationException("The native code editor does not expose TextPattern2.");

    private IValueProvider? NativeValueProvider =>
        GetNativePattern<IValueProvider>(PatternInterface.Value);

    private T? GetNativePattern<T>(PatternInterface patternInterface)
        where T : class =>
        _owner.GetNativeEditorAutomationPeer()?.GetPattern(patternInterface) as T;
}

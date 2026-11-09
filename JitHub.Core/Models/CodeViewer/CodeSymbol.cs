using JitHub.WinUI.Helpers;

namespace JitHub.Models.CodeViewer;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial record CodeSymbol(
    string Name,
    string Kind,
    int LineNumber)
{
    public string LocationText => LocalizedResourceText.Format("RepoCode/Outline/Line", "Line {0}", LineNumber);
    public string AutomationName => LocalizedResourceText.Format(
        "RepoCode/Outline/AutomationName",
        "{0} {1}, line {2}",
        Kind,
        Name,
        LineNumber);
    public string AutomationId => RepoCodeAutomation.CreateId(
        "RepoCodeOutlineItem",
        $"symbol:{Kind}:{Name}:{LineNumber}");
}

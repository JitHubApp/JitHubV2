using JitHub.Models.CodeViewer;
using JitHub.WinUI.Helpers;

namespace JitHub.WinUI.ViewModels.CodeViewer;

/// <summary>One segment in the breadcrumb path (e.g. repo root, folder, file).</summary>
public sealed record BreadcrumbSegment(string Label, string Path, bool IsRoot)
{
    public string AutomationId => RepoCodeAutomation.CreateId(
        "RepoCodeBreadcrumbSegment",
        IsRoot ? $"root:{Label}" : $"path:{Path}");

    public string AutomationName => IsRoot
        ? LocalizedResourceText.Format(
            "RepoCode/Breadcrumb/OpenRootAutomationName",
            "Open repository root {0}",
            Label)
        : LocalizedResourceText.Format(
            "RepoCode/Breadcrumb/OpenPathAutomationName",
            "Open {0}",
            Label);

    public string AutomationPath => IsRoot ? Label : Path;
}

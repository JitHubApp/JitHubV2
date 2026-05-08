namespace JitHub.WinUI.ViewModels.CodeViewer;

/// <summary>One segment in the breadcrumb path (e.g. repo root, folder, file).</summary>
public sealed record BreadcrumbSegment(string Label, string Path, bool IsRoot);

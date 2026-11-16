using FlaUI.Core.AutomationElements;

namespace MarkdownRenderer.Sample.Automation.Uia;

/// <summary>
/// Writes audit screenshots under artifacts/screenshots/markdown-audit.
/// </summary>
internal static class AuditScreenshots
{
    internal static void CaptureAuditScreenshot(AutomationElement element, string fileName)
    {
        string artifactDir = Path.Combine(
            Path.GetFullPath("."), "artifacts", "screenshots", "markdown-audit");
        Directory.CreateDirectory(artifactDir);
        string path = Path.Combine(artifactDir, fileName);
        using var image = FlaUI.Core.Capturing.Capture.Element(
            element, new FlaUI.Core.Capturing.CaptureSettings());
        image.ToFile(path);
        Console.WriteLine($"[automation] screenshot: {path}");
    }
}

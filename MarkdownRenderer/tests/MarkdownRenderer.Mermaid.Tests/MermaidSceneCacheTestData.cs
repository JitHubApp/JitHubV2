using System.Globalization;
using System.Text;
using MarkdownRenderer.Mermaid;

namespace MarkdownRenderer.Mermaid.Tests;

/// <summary>Keys, scenes and render results for scene cache tests.</summary>
internal static class MermaidSceneCacheTestData
{
    internal static MermaidSceneCacheKey CreateKey(
        string source,
        MermaidRenderOptions? options = null) =>
        MermaidSceneCacheKey.Create(
            Encoding.UTF8.GetBytes(source),
            options ?? MermaidRenderOptions.Default,
            MermaidFontCatalog.Default,
            CultureInfo.InvariantCulture);

    internal static MermaidScene CreateScene(string text) => new(
        MermaidSceneVersion.Current,
        new MermaidViewport(0, 0, 100, 40),
        [text],
        [],
        [],
        [],
        [],
        [],
        [],
        []);

    internal static MermaidRenderResult Success(MermaidScene scene, string source) =>
        new(MermaidRenderStatus.Success, scene, scene.Diagnostics, source);

    internal static MermaidRenderResult AdmissionRejected(string source) =>
        new(
            MermaidRenderStatus.Overloaded,
            null,
            [new MermaidDiagnostic("MMR0013", MermaidDiagnosticSeverity.Error, "overloaded")],
            source);

    internal static MermaidRenderResult DeadlineExceeded(string source) =>
        new(
            MermaidRenderStatus.TimedOut,
            null,
            [new MermaidDiagnostic("MMR0007", MermaidDiagnosticSeverity.Error, "timed out")],
            source);

    internal static long FutureDeadline() => MermaidDeadline.Start(TimeSpan.FromMinutes(1));
}

using System.Globalization;

namespace MarkdownRenderer.Mermaid;

/// <summary>
/// A Mermaid theme request interpreted by the native presentation stage. <see cref="Default"/>
/// and <see cref="Light"/> use Mermaid's documented default palette; the remaining named values
/// use the corresponding pinned Mermaid palette. Windows High Contrast colors are applied by the
/// host drawing layer without changing these authored colors.
/// </summary>
public enum MermaidThemeVariant
{
    Default,
    Light,
    Dark,
    HighContrast,
    Forest,
    Neutral,
}

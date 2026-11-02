using MarkdownRenderer.Hosting;

namespace MarkdownRenderer.Controls;

/// <summary>The requested language inputs <see cref="RendererLocalization"/> reads from the control.</summary>
internal interface IRendererLocalizationHost
{
    /// <summary>The control's own XAML language.</summary>
    string Language { get; }

    /// <summary>The language of the current Markdown environment snapshot.</summary>
    string? EnvironmentLanguage { get; }

    IMarkdownStringProvider? StringProvider { get; }
}

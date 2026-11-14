using MarkdownRenderer.Controls;

namespace JitHub.WinUI.Services.Markdown.Hosting;

/// <summary>What <see cref="MarkdownRendererInitialLayoutGate"/> needs from its Markdown host.</summary>
internal interface IMarkdownRendererLayoutGateHost
{
    /// <summary>Gets the renderer the host currently owns.</summary>
    MarkdownRendererControl? CurrentRenderer { get; }

    /// <summary>Gets whether the host is loaded and not shutting down.</summary>
    bool IsActive { get; }

    /// <summary>Called once the current renderer has completed its first measured layout.</summary>
    void OnInitialLayoutReady();
}

using System;
using MarkdownRenderer.Images;

namespace JitHub.Markdown;

/// <summary>The product host observer: it records nothing and captures no links.</summary>
public sealed class NoMarkdownHostObserver : IMarkdownHostObserver
{
    public static NoMarkdownHostObserver Instance { get; } = new();

    private NoMarkdownHostObserver()
    {
    }

    public void OnHostReady(string automationId)
    {
    }

    public void OnImageUnavailable(string automationId, string source, MarkdownImageUnavailableReason reason)
    {
    }

    public void OnRenderFailed(string automationId, Exception exception)
    {
    }

    public bool TryCaptureLinkRoute(string automationId, Uri uri, string disposition) => false;
}

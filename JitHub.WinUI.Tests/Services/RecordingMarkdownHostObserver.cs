using System;
using System.Collections.Generic;
using JitHub.Markdown;
using JitHub.Services.Markdown;
using MarkdownRenderer.Images;

namespace JitHub.WinUI.Tests.Services;

/// <summary>Records Markdown host milestones; optionally captures link activations.</summary>
internal sealed class RecordingMarkdownHostObserver : IMarkdownHostObserver
{
    public bool CaptureLinks { get; set; }

    public List<string> Calls { get; } = [];

    public List<(string AutomationId, string Source, MarkdownImageUnavailableReason Reason)> ImagesUnavailable { get; } = [];

    public List<(string AutomationId, Exception Exception)> RenderFailures { get; } = [];

    public List<(string AutomationId, Uri Uri, string Disposition)> LinkRoutes { get; } = [];

    public void OnHostReady(string automationId) => Calls.Add("host-ready");

    public void OnImageUnavailable(string automationId, string source, MarkdownImageUnavailableReason reason)
    {
        Calls.Add("image-unavailable");
        ImagesUnavailable.Add((automationId, source, reason));
    }

    public void OnRenderFailed(string automationId, Exception exception)
    {
        Calls.Add("render-failed");
        RenderFailures.Add((automationId, exception));
    }

    public bool TryCaptureLinkRoute(string automationId, Uri uri, string disposition)
    {
        Calls.Add("link-route");
        LinkRoutes.Add((automationId, uri, disposition));
        return CaptureLinks;
    }
}

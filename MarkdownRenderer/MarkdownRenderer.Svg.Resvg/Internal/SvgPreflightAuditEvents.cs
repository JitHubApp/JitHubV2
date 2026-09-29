using System.Diagnostics.Tracing;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// Audit-only preflight rejection evidence. It contains no source bytes or URL;
/// the SHA-256 identifies the exact received payload across a live recurrence.
/// </summary>
[EventSource(Name = "MarkdownRenderer.Svg.Resvg.Preflight")]
internal sealed class SvgPreflightAuditEvents : EventSource
{
    internal static readonly SvgPreflightAuditEvents Log = new();

    private SvgPreflightAuditEvents()
    {
    }

    [Event(1, Level = EventLevel.Warning)]
    public void Rejected(string reason, int sourceByteLength, string sourceSha256)
    {
        if (IsEnabled())
            WriteEvent(1, reason, sourceByteLength, sourceSha256);
    }
}

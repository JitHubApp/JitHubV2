using System;
using JitHub.Markdown.Hosting;

namespace JitHub.Markdown.Accessibility;

/// <summary>
/// Resolves the UI Automation identities of one Markdown form: the prefix every
/// interactive peer is scoped by, the peer ids, and the preview host identity.
/// </summary>
/// <remarks>
/// Automation identity is not document identity. A form without an explicit or
/// attached automation id gets a fallback that is unique to the form instance,
/// so two anonymous forms on one page never share peer ids.
/// </remarks>
public sealed class MarkdownFormAutomationIdentity
{
    /// <summary>Creates the identity of a new form with a fresh fallback scope.</summary>
    public MarkdownFormAutomationIdentity()
        : this(Guid.NewGuid())
    {
    }

    /// <param name="instance">The form instance's identity, used only for the fallback scope.</param>
    internal MarkdownFormAutomationIdentity(Guid instance)
    {
        FallbackInstanceId = AutomationScopedId.Create("MarkdownForm", $"runtime:{instance:N}");
    }

    /// <summary>Gets the prefix used when the form has no explicit or attached automation id.</summary>
    internal string FallbackInstanceId { get; }

    /// <summary>
    /// Returns the prefix for the form's peers: the explicit instance id, then the
    /// attached automation id, then the fallback.
    /// </summary>
    public string ResolvePrefix(string? automationInstanceId, string? attachedAutomationId)
    {
        if (!string.IsNullOrWhiteSpace(automationInstanceId))
        {
            return automationInstanceId.Trim();
        }

        return string.IsNullOrWhiteSpace(attachedAutomationId)
            ? FallbackInstanceId
            : attachedAutomationId.Trim();
    }

    /// <summary>Returns the preview viewer's automation instance id for a prefix.</summary>
    public static string GetPreviewInstanceId(string prefix) => $"{prefix}_Preview";

    /// <summary>Returns the preview viewer's Markdown host automation id for a prefix.</summary>
    public static string GetPreviewHostId(string prefix) =>
        MarkdownHostContract.GetAutomationId(MarkdownHostContract.EditorPreview, GetPreviewInstanceId(prefix));

    /// <summary>Returns the automation ids of the form's interactive peers for a prefix.</summary>
    public static MarkdownFormPeerAutomationIds GetPeerIds(string prefix) => new(
        Mode: $"{prefix}_Mode",
        WriteMode: $"{prefix}_Mode_Write",
        PreviewMode: $"{prefix}_Mode_Preview",
        Editor: $"{prefix}_Editor",
        Preview: GetPreviewInstanceId(prefix));
}

namespace MarkdownRenderer.Controls.Internal.Embeds;

// Code block copy buttons are overlay-hosted native controls, but they are
// renderer chrome rather than user-authored embeds. Keep them on a separate
// realization track so RealizedEmbedCount remains embed-only.
internal enum CodeBlockHostedElementKind
{
    Copy,
}

using System;
using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Diagnostics.Internal;
using MarkdownRenderer.Layout.Internal.CodeBlocks;
using Windows.ApplicationModel.DataTransfer;

namespace MarkdownRenderer.Controls.Embeds;

/// <summary>
/// Copies a code block's text to the clipboard from its copy button or from
/// UI Automation, shows the button's copied feedback, and reports the copy.
/// </summary>
internal sealed class CodeBlockClipboard
{
    private readonly MarkdownRendererControl _control;
    private readonly EmbedRealization _embeds;
    private readonly RendererEvents _events;

    internal CodeBlockClipboard(MarkdownRendererControl control, EmbedRealization embeds, RendererEvents events)
    {
        _control = control;
        _embeds = embeds;
        _events = events;
    }

    internal void Copy(CodeBlockActionPlan plan)
    {
        bool succeeded = false;
        try
        {
            var package = new DataPackage();
            package.SetText(plan.Box.CodeText);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            plan.ShowCopiedFeedback(_embeds.Context);
            succeeded = true;
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine($"[MarkdownRendererControl] Code block copy failed: {ex.Message}");
        }

        _events.RaiseCopyCompleted(MarkdownCopyKind.CodeBlock, succeeded);
    }

    internal void CopyFromAutomation(CodeBlockBox box)
    {
        if (_control.CodeBlockChromeMode == CodeBlockChromeMode.PlainMarkdown || !box.IsCopyButtonEnabled)
            return;

        foreach (CodeBlockActionPlan plan in _embeds.CodeBlockActionPlans)
        {
            if (ReferenceEquals(plan.Box, box))
            {
                Copy(plan);
                return;
            }
        }

        // UI Automation can invoke an off-screen code block before its
        // viewport-owned XAML button is realized. Copy directly without
        // realizing an overlay element or mutating the UIA child tree.
        Copy(new CodeBlockActionPlan { Box = box });
    }
}

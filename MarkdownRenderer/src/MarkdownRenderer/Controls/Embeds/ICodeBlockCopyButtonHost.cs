using Microsoft.UI.Xaml;

namespace MarkdownRenderer.Controls.Embeds;

/// <summary>
/// The control's appearance settings for code block copy buttons.
/// </summary>
internal interface ICodeBlockCopyButtonHost
{
    /// <summary>The host-supplied button style, or null for the default.</summary>
    Style? CodeBlockCopyButtonStyle { get; }

    /// <summary>
    /// Resolves the button's accessible name and tooltip. May call a host
    /// string provider.
    /// </summary>
    string ResolveCodeBlockCopyButtonLabel(bool copied);
}

using Microsoft.UI.Xaml;

namespace MarkdownRenderer.Controls.Focus;

/// <summary>What hosted element focus needs from the control.</summary>
internal interface IHostedElementFocusHost
{
    /// <summary>The control's own XAML focus state.</summary>
    FocusState FocusState { get; }

    /// <summary>The XAML root, or null while the control is not in a tree.</summary>
    XamlRoot? XamlRoot { get; }

    /// <summary>The layout revision whose realization plans were last applied.</summary>
    long AppliedLazyLayoutRevision { get; }

    /// <summary>Gives the control itself keyboard focus.</summary>
    bool FocusRenderer();
}

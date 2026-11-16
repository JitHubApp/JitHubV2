namespace MarkdownRenderer.Theming.Internal.Environment;

/// <summary>Receives shared window-environment changes without being rooted by the monitor.</summary>
internal interface IMarkdownEnvironmentListener
{
    void OnMarkdownEnvironmentChanged(
        in MarkdownEnvironmentSnapshot snapshot,
        MarkdownEnvironmentChange changes);
}

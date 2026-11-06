using System;
using System.Windows.Input;
using MarkdownRenderer.Hosting;

namespace MarkdownRenderer.Sample;

/// <summary>
/// Supplies the task-list toggle command, which edits the task marker in the
/// editor's source so the editor stays the document's single source of truth.
/// </summary>
internal sealed class SampleTaskCommandProvider(
    Func<string> getSource,
    Action<string> setSource) : IMarkdownCommandProvider
{
    private readonly ICommand _command = new SampleTaskCommand(getSource, setSource);

    public ICommand? GetCommand(MarkdownCommandContext context) =>
        context.Kind == MarkdownCommandKind.ToggleTask ? _command : null;
}

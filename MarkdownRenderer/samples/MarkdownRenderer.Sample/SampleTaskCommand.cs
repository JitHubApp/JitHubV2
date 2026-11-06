using System;
using System.Windows.Input;
using MarkdownRenderer.Hosting;

namespace MarkdownRenderer.Sample;

/// <summary>
/// Replaces a task marker (<c>[ ]</c> or <c>[x]</c>) in the source with the
/// checked state the renderer requests.
/// </summary>
internal sealed class SampleTaskCommand(
    Func<string> getSource,
    Action<string> setSource) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) =>
        parameter is MarkdownCommandContext context &&
        TryCreateReplacement(getSource(), context, out _);

    public void Execute(object? parameter)
    {
        if (parameter is not MarkdownCommandContext context)
            return;

        string source = getSource();
        if (!TryCreateReplacement(source, context, out string replacement))
            return;

        setSource(string.Concat(
            source.AsSpan(0, context.SourceRange.Start),
            replacement,
            source.AsSpan(context.SourceRange.End)));
    }

    private static bool TryCreateReplacement(
        string source,
        MarkdownCommandContext context,
        out string replacement)
    {
        replacement = context.Target switch
        {
            "checked" => "[x]",
            "unchecked" => "[ ]",
            _ => string.Empty,
        };
        SourceSpan range = context.SourceRange;
        return replacement.Length == 3 &&
               range.Length == 3 &&
               range.Start >= 0 &&
               range.End <= source.Length &&
               source[range.Start] == '[' &&
               source[range.End - 1] == ']';
    }
}

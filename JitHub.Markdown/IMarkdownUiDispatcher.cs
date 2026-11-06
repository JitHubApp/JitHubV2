using System;

namespace JitHub.Markdown;

/// <summary>
/// The UI thread a Markdown host lives on.
/// </summary>
public interface IMarkdownUiDispatcher
{
    /// <summary>Gets whether the caller is already on the UI thread.</summary>
    bool HasThreadAccess { get; }

    /// <summary>Queues <paramref name="callback"/> on the UI thread.</summary>
    /// <param name="callback">The work to run on the UI thread.</param>
    /// <returns><see langword="false"/> when the dispatcher rejected the work.</returns>
    bool TryEnqueue(Action callback);
}

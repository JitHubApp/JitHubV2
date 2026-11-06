namespace MarkdownRenderer.Controls.Threading;

/// <summary>The priority of work posted through <see cref="IMarkdownUiScheduler"/>.</summary>
internal enum MarkdownUiPriority
{
    /// <summary>Runs after input and layout work.</summary>
    Low,

    /// <summary>The default priority.</summary>
    Normal,

    /// <summary>Runs before normal-priority work.</summary>
    High,
}

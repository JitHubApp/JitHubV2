using MarkdownRenderer.Layout;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// One entry of the legacy keyboard focus plan built by <see cref="LegacySnapshotIndexes"/>.
/// </summary>
internal sealed record LegacyFocusableEntry(FocusableItem Item, IHorizontalOverflowBox? Overflow);

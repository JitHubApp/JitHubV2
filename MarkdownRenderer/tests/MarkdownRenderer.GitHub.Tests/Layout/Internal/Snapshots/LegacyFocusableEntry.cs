using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.Focus;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.Snapshots;

/// <summary>
/// One entry of the legacy keyboard focus plan built by <see cref="LegacySnapshotIndexes"/>.
/// </summary>
internal sealed record LegacyFocusableEntry(FocusableItem Item, IHorizontalOverflowBox? Overflow);

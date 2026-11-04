using System;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// The disposal and publication failures of one native-resource retirement.
/// </summary>
internal readonly struct NativeRetirementFailurePair(
    Exception? DisposalFailure,
    Exception? PublicationFailure)
{
    public Exception? DisposalFailure { get; init; } = DisposalFailure;
    public Exception? PublicationFailure { get; init; } = PublicationFailure;
}

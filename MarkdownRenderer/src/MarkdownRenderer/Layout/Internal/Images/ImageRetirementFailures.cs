using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MarkdownRenderer.Layout.Internal.Images;

/// <summary>
/// The bounded failure list of one image box retirement: at most
/// <see cref="MaximumDetails"/> exceptions are kept, and the rest are counted
/// and reported as one summary when retirement completes.
/// </summary>
internal sealed class ImageRetirementFailures
{
    /// <summary>The most failures kept with their details.</summary>
    internal const int MaximumDetails = 8;

    private List<Exception>? _failures;
    private int _omitted;

    /// <summary>Keeps <paramref name="exception"/>, or counts it once the list is full.</summary>
    internal void Add(Exception exception)
    {
        if (_failures is null || _failures.Count < MaximumDetails)
            (_failures ??= []).Add(exception);
        else
            _omitted++;
    }

    /// <summary>
    /// Adds each failure <paramref name="other"/> kept, in order, then counts
    /// the failures it omitted.
    /// </summary>
    internal void MergeFrom(ImageRetirementFailures other)
    {
        if (other._failures is { } failures)
        {
            foreach (Exception failure in failures)
                Add(failure);
        }

        _omitted += other._omitted;
    }

    /// <summary>
    /// Completes <paramref name="completion"/>: successfully with no
    /// failures, with the single failure, or with an aggregate whose last
    /// entry summarizes any omitted failures.
    /// </summary>
    internal void Complete(TaskCompletionSource completion)
    {
        List<Exception>? failures = _failures;
        int omittedFailures = _omitted;
        if (omittedFailures > 0)
        {
            bool replacedDetail = failures is { Count: >= MaximumDetails };
            if (replacedDetail)
                omittedFailures++;
            var summary = new InvalidOperationException(
                $"{omittedFailures} additional image retirement failures were omitted.");
            if (replacedDetail)
                failures![^1] = summary;
            else
                (failures ??= []).Add(summary);
        }

        if (failures is null)
        {
            completion.TrySetResult();
        }
        else
        {
            completion.TrySetException(failures.Count == 1
                ? failures[0]
                : new AggregateException("Image box retirement failed.", failures));
        }
    }
}

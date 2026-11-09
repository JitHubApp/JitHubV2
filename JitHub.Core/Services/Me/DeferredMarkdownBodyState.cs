using System;

namespace JitHub.Services;

public sealed class DeferredMarkdownBodyState
{
    public const int DefaultRealizationThreshold = 12_000;
    public const int DefaultPreviewLength = 4_000;

    private readonly int _realizationThreshold;
    private readonly int _previewLength;

    public DeferredMarkdownBodyState(
        int realizationThreshold = DefaultRealizationThreshold,
        int previewLength = DefaultPreviewLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(realizationThreshold);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(previewLength);
        _realizationThreshold = realizationThreshold;
        _previewLength = Math.Min(previewLength, realizationThreshold);
    }

    public string FullText { get; private set; } = string.Empty;

    public bool IsExpanded { get; private set; }

    public bool IsDeferred => !IsExpanded && FullText.Length > _realizationThreshold;

    public bool IsMarkdownRealized => !IsDeferred;

    public string PreviewText => IsDeferred
        ? FullText[..Math.Min(_previewLength, FullText.Length)]
        : FullText;

    public void Update(string? text)
    {
        string next = text ?? string.Empty;
        if (string.Equals(FullText, next, StringComparison.Ordinal))
        {
            return;
        }

        FullText = next;
        IsExpanded = false;
    }

    public bool Expand()
    {
        if (!IsDeferred)
        {
            return false;
        }

        IsExpanded = true;
        return true;
    }
}

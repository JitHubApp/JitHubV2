namespace MarkdownRenderer.Layout.Boxes;

/// <summary>Point-in-time accounting of one <see cref="SharedCanvasBitmapCache"/>.</summary>
/// <param name="RetainedBytes">Bytes the LRU currently retains, including key weight.</param>
/// <param name="BudgetBytes">The LRU's retained-byte budget.</param>
/// <param name="LivePixelWeightBytes">Pixel weight of every bitmap still owned by a lease or the LRU.</param>
/// <param name="UncertainDisposePixelWeightBytes">Pixel weight whose native disposal failed.</param>
/// <param name="DisposalFailureCount">Contained cache-reference disposal failures.</param>
/// <param name="LastDisposalFailureHResult">HRESULT of the most recent contained failure.</param>
internal readonly record struct SharedCanvasBitmapCacheSnapshot(
    long RetainedBytes,
    long BudgetBytes,
    long LivePixelWeightBytes,
    long UncertainDisposePixelWeightBytes,
    int DisposalFailureCount,
    int LastDisposalFailureHResult);

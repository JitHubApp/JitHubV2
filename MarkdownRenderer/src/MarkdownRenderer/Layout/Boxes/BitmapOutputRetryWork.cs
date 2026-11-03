namespace MarkdownRenderer.Layout.Boxes;

/// <summary>The waiter and epoch a queued source-free bitmap-output retry prepares.</summary>
internal readonly struct BitmapOutputRetryWork(ImageBitmapOutputWaiter Owner, long Epoch)
{
    public ImageBitmapOutputWaiter Owner { get; init; } = Owner;
    public long Epoch { get; init; } = Epoch;
}

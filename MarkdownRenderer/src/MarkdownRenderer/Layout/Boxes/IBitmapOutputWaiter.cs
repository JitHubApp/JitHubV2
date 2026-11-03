namespace MarkdownRenderer.Layout.Boxes;

internal interface IBitmapOutputWaiter
{
    void OnBitmapOutputCapacityAvailable(long capacityVersion);
}

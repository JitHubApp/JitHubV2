namespace MarkdownRenderer.Layout.Internal.Images.BitmapOutput;

internal interface IBitmapOutputWaiter
{
    void OnBitmapOutputCapacityAvailable(long capacityVersion);
}

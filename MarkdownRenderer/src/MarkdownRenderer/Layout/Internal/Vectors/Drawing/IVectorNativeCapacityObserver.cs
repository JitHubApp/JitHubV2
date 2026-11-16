namespace MarkdownRenderer.Layout.Internal.Vectors.Drawing;

internal interface IVectorNativeCapacityObserver
{
    void OnCapacityAvailable(long generation);
}

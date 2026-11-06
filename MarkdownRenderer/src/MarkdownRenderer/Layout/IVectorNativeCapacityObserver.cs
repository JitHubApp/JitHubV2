namespace MarkdownRenderer.Layout;

internal interface IVectorNativeCapacityObserver
{
    void OnCapacityAvailable(long generation);
}

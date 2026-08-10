namespace JitHub.Services.Layout;

public static class ListViewScrollAnchorPolicy
{
    public static bool IsAtScrollableBottom(double scrollableHeight, double verticalOffset) =>
        scrollableHeight > 0 && verticalOffset >= scrollableHeight - 2;

    public static bool ShouldRestore(bool userInteracted) => !userInteracted;
}

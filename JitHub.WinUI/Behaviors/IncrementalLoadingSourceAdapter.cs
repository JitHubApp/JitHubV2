using Microsoft.UI.Xaml.Data;

namespace JitHub.WinUI.Behaviors;

internal static class IncrementalLoadingSourceAdapter
{
    public static ISupportIncrementalLoading? Resolve(
        ISupportIncrementalLoading? explicitSource,
        object? itemsSource,
        object? host)
    {
        return explicitSource ??
            itemsSource as ISupportIncrementalLoading ??
            (host as IIncrementalLoadingHost)?.IncrementalLoadingSource;
    }

    public static bool IsLoading(ISupportIncrementalLoading source)
        => source is IIncrementalLoadingActivity { IsLoading: true };
}

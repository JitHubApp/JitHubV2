using Microsoft.UI.Xaml.Data;

namespace JitHub.WinUI.Behaviors;

public interface IIncrementalLoadingHost
{
    ISupportIncrementalLoading? IncrementalLoadingSource { get; }
}

public interface IIncrementalLoadingActivity
{
    bool IsLoading { get; }
}

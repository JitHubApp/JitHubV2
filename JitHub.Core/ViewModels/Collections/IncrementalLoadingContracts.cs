using Microsoft.UI.Xaml.Data;

namespace JitHub.Core.ViewModels.Collections;

public interface IIncrementalLoadingHost
{
    ISupportIncrementalLoading? IncrementalLoadingSource { get; }
}

public interface IIncrementalLoadingActivity
{
    bool IsLoading { get; }
}

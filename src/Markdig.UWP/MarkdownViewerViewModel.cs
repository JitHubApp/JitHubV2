using CommunityToolkit.Mvvm.ComponentModel;

namespace Markdig.UWP;

internal partial class MarkdownViewerViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _loading;
}

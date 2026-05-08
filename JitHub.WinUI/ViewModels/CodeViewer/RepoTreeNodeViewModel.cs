using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using JitHub.Models.CodeViewer;
using JitHub.Services.CodeViewer;

namespace JitHub.WinUI.ViewModels.CodeViewer;

public sealed partial class RepoTreeNodeViewModel : ObservableObject
{
    public string Name { get; }
    public string Path { get; }
    public string Sha { get; }
    public bool IsDirectory { get; }
    public long? Size { get; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    public partial bool IsLoadingChildren { get; set; }

    public bool ChildrenLoaded { get; set; }

    public string LanguageId { get; }

    public ObservableCollection<RepoTreeNodeViewModel> Children { get; } = [];

    public RepoTreeNodeViewModel? Parent { get; }

    public RepoTreeNodeViewModel(RepoTreeNode model, ILanguageIdResolver languageResolver, RepoTreeNodeViewModel? parent = null)
    {
        Name = model.Name;
        Path = model.Path;
        Sha = model.Sha ?? string.Empty;
        IsDirectory = model.IsDirectory;
        Size = model.Size;
        Parent = parent;

        LanguageId = IsDirectory
            ? string.Empty
            : languageResolver.Resolve(model.Name);
    }
}

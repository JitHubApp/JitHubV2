using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using JitHub.Models.CodeViewer;
using JitHub.Services.CodeViewer;
using JitHub.WinUI.Helpers;

namespace JitHub.WinUI.ViewModels.CodeViewer;

public sealed partial class RepoTreeNodeViewModel : ObservableObject
{
    public string Name { get; }
    public string Path { get; }
    public string Sha { get; private set; }
    public bool IsDirectory { get; }
    public long? Size { get; private set; }
    public string AutomationId => RepoCodeAutomation.CreateId("RepoCodeTreeItem", $"path:{Path}");
    public string AutomationName => IsDirectory
        ? LocalizedResourceText.Format("RepoCode/Tree/FolderAutomationName", "{0}, folder", Name)
        : LocalizedResourceText.Format("RepoCode/Tree/FileAutomationName", "{0}, file", Name);

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

    public void UpdateMetadata(RepoTreeNode model)
    {
        Sha = model.Sha ?? string.Empty;
        Size = model.Size;
    }

    public override string ToString() => Name;
}

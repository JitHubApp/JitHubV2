using JitHub.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.WinUI.Views.Controls.Commit;

public sealed partial class CommitDiffRowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? FileHeaderTemplate { get; set; }

    public DataTemplate? HunkHeaderTemplate { get; set; }

    public DataTemplate? DiffLineTemplate { get; set; }

    public DataTemplate? UnavailableDiffTemplate { get; set; }

    public DataTemplate? SearchNoResultsTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => SelectTemplateForItem(item);

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateForItem(item);

    private DataTemplate? SelectTemplateForItem(object item) => item is CommitDiffRow row
        ? row.Kind switch
        {
            CommitDiffRowKind.FileHeader => FileHeaderTemplate,
            CommitDiffRowKind.HunkHeader => HunkHeaderTemplate,
            CommitDiffRowKind.DiffLine => DiffLineTemplate,
            CommitDiffRowKind.UnavailableDiff => UnavailableDiffTemplate,
            CommitDiffRowKind.SearchNoResults => SearchNoResultsTemplate,
            _ => DiffLineTemplate
        }
        : null;
}

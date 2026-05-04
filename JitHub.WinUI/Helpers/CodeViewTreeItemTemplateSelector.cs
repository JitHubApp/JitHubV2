using JitHub.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.WinUI.Helpers
{
    public partial class CodeViewTreeItemTemplateSelector : DataTemplateSelector
    {
        public DataTemplate? FolderTemplate { get; set; }
        public DataTemplate? FileTemplate { get; set; }

        protected override DataTemplate? SelectTemplateCore(object item)
        {
            if (item is not RepoContentNode node)
            {
                return FileTemplate ?? FolderTemplate;
            }

            return node.IsDir
                ? FolderTemplate ?? FileTemplate
                : FileTemplate ?? FolderTemplate;
        }
    }
}


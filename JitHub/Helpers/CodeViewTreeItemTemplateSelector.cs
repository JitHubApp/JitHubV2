using JitHub.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.Helpers
{
    public class CodeViewTreeItemTemplateSelector : DataTemplateSelector
    {
        public DataTemplate FolderTemplate { get; set; }
        public DataTemplate FileTemplate { get; set; }
        protected override DataTemplate SelectTemplateCore(object item)
        {
            switch (((RepoContentNode)item).IsDir)
            {
                case true:
                    return FolderTemplate;
                case false:
                    return FileTemplate;
                default:
                    return FileTemplate;
            }
        }
    }
}

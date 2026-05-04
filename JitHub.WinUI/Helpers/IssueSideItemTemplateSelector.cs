using JitHub.Models;
using JitHub.Models.Base;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.WinUI.Helpers
{
    public partial class IssueSideItemTemplateSelector : DataTemplateSelector
    {
        public DataTemplate? UserTemplate { get; set; }
        public DataTemplate? LabelTemplate { get; set; }

        protected override DataTemplate? SelectTemplateCore(object item)
        {
            if (item is not SelectableItem selectableItem)
            {
                return UserTemplate ?? LabelTemplate;
            }

            return selectableItem.Type switch
            {
                "User" => UserTemplate,
                "Label" => LabelTemplate,
                _ => UserTemplate ?? LabelTemplate
            };
        }
    }
}


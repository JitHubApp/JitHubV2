using JitHub.WinUI.Models.Filter;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.WinUI.Views.TemplateSelectors
{
    public partial class FilterSelectionTemplateSelector : DataTemplateSelector
    {
        public DataTemplate? DropdownTemplate { get; set; }
        public DataTemplate? DateTemplate { get; set; }
        public DataTemplate? TextTemplate { get; set; }

        protected override DataTemplate? SelectTemplateCore(object item)
        {
            if (item is not FilterUnit filterUnit)
            {
                return DropdownTemplate ?? TextTemplate ?? DateTemplate;
            }

            switch (filterUnit.Type)
            {
                case nameof(DropdownFilter):
                    return DropdownTemplate;
                case nameof(DateFilter):
                    return DateTemplate;
                case nameof(TextFilter):
                    return TextTemplate;
                default:
                    return DropdownTemplate ?? TextTemplate ?? DateTemplate;
            }
        }
    }
}


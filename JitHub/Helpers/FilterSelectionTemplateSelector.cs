using JitHub.Models.Filter;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.Helpers
{
    public class FilterSelectionTemplateSelector : DataTemplateSelector
    {
        public DataTemplate DropdownTemplate { get; set; }
        public DataTemplate DateTemplate { get; set; }
        public DataTemplate TextTemplate { get; set; }
        protected override DataTemplate SelectTemplateCore(object item)
        {
            switch (((FilterUnit)item).Type)
            {
                case nameof(DropdownFilter):
                    return DropdownTemplate;
                case nameof(DateFilter):
                    return DateTemplate;
                case nameof(TextFilter):
                    return TextTemplate;
                default:
                    return DropdownTemplate;
            }
        }
    }
}

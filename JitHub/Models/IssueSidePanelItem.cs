using JitHub.Models.Base;
using System.Collections.Generic;
using Windows.UI.Xaml;

namespace JitHub.Models
{
    public class IssueSidePanelItem
    {
        public string Header { get; }
        public ICollection<SelectableItem> Items { get; }
        public bool Show { get; }

        public IssueSidePanelItem(string header, ICollection<SelectableItem> items)
        {
            Header = header;
            Items = items;
            Show = Items.Count != 0;
        }
    }
}

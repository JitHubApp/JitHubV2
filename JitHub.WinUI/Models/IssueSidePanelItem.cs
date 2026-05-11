using JitHub.Models.Base;
using System.Collections.Generic;
using System.Linq;

namespace JitHub.Models
{
    [WinRT.GeneratedBindableCustomProperty]
    public partial class IssueSidePanelItem
    {
        public string Header { get; }
        public List<SelectableItem> Items { get; }
        public bool Show { get; }

        public IssueSidePanelItem(string header, IEnumerable<SelectableItem> items)
        {
            Header = header;
            Items = items?.ToList() ?? new List<SelectableItem>();
            Show = Items.Count > 0;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace JitHub.Models.Filter
{
    public class Selection
    {
        public string DisplayMember { get; set; }
        public string SelectedValue { get; set; }
        public object Value { get; set; }
        public bool NotSelected { get; }

        public Selection(string displayMember, string selectedValue, object value, bool notSelected = false)
        {
            DisplayMember = displayMember;
            SelectedValue = selectedValue;
            Value = value;
            NotSelected = notSelected;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace JitHub.Models.Filter
{
    public class DropdownFilter : FilterUnit
    {
        private bool _editable;
        private ICollection<Selection> _selections;
        private Selection _selected;
        private Selection _defaultSelection;
        public bool Editable
        {
            get => _editable;
            set => SetProperty(ref _editable, value);
        }
        public ICollection<Selection> Selections
        {
            get => _selections;
            set => SetProperty(ref _selections, value);
        }
        public Selection Selected
        {
            get => _selected;
            set => SetProperty(ref _selected, value);
        }
        public override bool DefaultSelected => Selected == _defaultSelection;

        public DropdownFilter(string name, bool editable = false)
        {
            Name = name;
            Editable = editable;
            Type = nameof(DropdownFilter);
            Selections = new ObservableCollection<Selection>();
            _defaultSelection = new Selection("Selection one", null, null, true);
            Selections.Add(_defaultSelection);
            Selected = _defaultSelection;
        }

        public void Add(Selection selection)
        {
            Selections.Add(selection);
        }

        public void AddRange(ICollection<Selection> selections)
        {
            foreach (var selection in selections)
            {
                Add(selection);
            }
        }

        public override void SetDefault()
        {
            Selected = _defaultSelection;
        }
    }
}

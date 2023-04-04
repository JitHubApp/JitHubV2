using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;

namespace JitHub.Models.Base
{
    public class SelectableItem : ObservableObject
    {
        private bool _selected;
        private bool _selectable = true;
        public bool Selected
        {
            get => _selected;
            set => SetProperty(ref _selected, value);
        }
        public bool Selectable
        {
            get => _selectable;
            set => SetProperty(ref _selectable, value);
        }
        public ICommand SelectionCommand { get; set; }
        public SelectableItem Item => this;
        public string Type { get; set; }
    }
}

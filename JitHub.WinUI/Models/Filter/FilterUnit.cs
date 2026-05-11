using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace JitHub.Models.Filter
{
    [WinRT.GeneratedBindableCustomProperty]
    public abstract partial class FilterUnit : ObservableObject
    {
        private string _name = string.Empty;
        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }
        public string Title
        {
            get => $"Filter by {Name}";
        }
        public abstract bool DefaultSelected { get; }
        public string Type { get; set; } = string.Empty;

        public abstract void SetDefault();
    }
}

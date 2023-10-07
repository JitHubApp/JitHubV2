using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace JitHub.Models.Filter
{
    public abstract class FilterUnit : ObservableObject
    {
        private string _name;
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
        public string Type { get; set; }

        public abstract void SetDefault();
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace JitHub.Models.Filter
{
    public class TextFilter : FilterUnit
    {
        private string _text;
        private string _defaultValue;
        public string Text
        {
            get => _text;
            set => SetProperty(ref _text, value);
        }

        public override bool DefaultSelected => Text == _defaultValue;

        public TextFilter(string name) : this(name, "") { }

        public TextFilter(string name, string defaultValue)
        {
            Name = name;
            _defaultValue = defaultValue;
            Type = nameof(TextFilter);
        }

        public override void SetDefault()
        {
            Text = _defaultValue;
        }
    }
}

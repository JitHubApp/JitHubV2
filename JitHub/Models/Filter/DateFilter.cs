using System;

namespace JitHub.Models.Filter
{
    public class DateFilter : FilterUnit
    {
        private DateTimeOffset _startDate;
        private DateTimeOffset _defaultDate;
        private string _placeholder;
        public DateTimeOffset StartDate
        {
            get => _startDate;
            set => SetProperty(ref _startDate, value);
        }
        public string Placeholder
        {
            get => _placeholder;
            set => SetProperty(ref _placeholder, value);
        }

        public override bool DefaultSelected => StartDate == _defaultDate;

        public DateFilter(DateTimeOffset startDate, string name, string placeholder) : this(startDate, startDate, name, placeholder) { }

        public DateFilter(DateTimeOffset startDate, DateTimeOffset defaultDate, string name, string placeholder)
        {
            StartDate = startDate;
            _defaultDate = defaultDate;
            Placeholder = placeholder;
            Name = name;
            Type = nameof(DateFilter);
        }

        public override void SetDefault()
        {
            StartDate = _defaultDate;
        }
    }
}

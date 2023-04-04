using System;
using Windows.UI.Xaml.Data;

namespace JitHub.Converters
{
    class TimeAgoConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value == null)
            {
                return null;
            }
            DateTime dateTime;
            if (value is DateTimeOffset dto)
            {
                dateTime = DateTime.Parse(dto.ToString());
            }
            else if (value is DateTime dt)
            {
                dateTime = dt;
            }
            else
            {
                dateTime = DateTime.Parse(value.ToString());
            }
            return ConvertDateToTimeAgoFormat(dateTime, parameter == null ? "Updated " : $"{(string)parameter} ");
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();

        private string ConvertDateToTimeAgoFormat(DateTime dt, string prefix)
        {
            var ts = new TimeSpan(DateTime.Now.Ticks - dt.Ticks);
            double delta = Math.Abs(ts.TotalSeconds);

            var languageLoader = new Windows.ApplicationModel.Resources.ResourceLoader();
            var stringToReturn = string.Empty;
            if (delta < 60)
            {
                if (ts.Seconds == 1)
                {
                    stringToReturn = languageLoader.GetString("aSecondAgo");
                }
                else
                {
                    stringToReturn = string.Format("{0} {1}",
                        ts.Seconds,
                        languageLoader.GetString("secondsAgo"));
                }
            }
            else if (delta < 120)
            {
                stringToReturn = languageLoader.GetString("aMinuteAgo");
            }
            else if (delta < 2700) // 45 * 60
            {
                stringToReturn = string.Format("{0} {1}",
                    ts.Minutes,
                    languageLoader.GetString("minutesAgo"));
            }
            else if (delta < 5400) // 90 * 60
            {
                stringToReturn = languageLoader.GetString("anHourAgo");
            }
            else if (delta < 86400) // 24 * 60 * 60
            {
                stringToReturn = string.Format("{0} {1}",
                    ts.Hours,
                    languageLoader.GetString("hoursAgo"));
            }
            else if (delta < 172800) // 48 * 60 * 60
            {
                stringToReturn = languageLoader.GetString("aDayAgo");
            }
            else if (delta < 2592000) // 30 * 24 * 60 * 60
            {
                stringToReturn = string.Format("{0} {1}",
                    ts.Days,
                    languageLoader.GetString("daysAgo"));
            }
            else if (delta < 31104000) // 12 * 30 * 24 * 60 * 60
            {
                var months = System.Convert.ToInt32(Math.Floor((double)ts.Days / 30));

                if (months <= 1)
                {
                    stringToReturn = languageLoader.GetString("oneMonthAgo");
                }
                else
                {
                    stringToReturn = string.Format("{0} {1}",
                        months,
                        languageLoader.GetString("monthsAgo"));
                }
            }
            else
            {
                int years = System.Convert.ToInt32(Math.Floor((double)ts.Days / 365));

                if (years <= 1)
                {
                    stringToReturn = languageLoader.GetString("oneYearAgo");
                }
                else
                {
                    stringToReturn = string.Format("{0} {1}",
                        years,
                        languageLoader.GetString("yearsAgo"));
                }
            }
            
            return String.IsNullOrEmpty(stringToReturn.Trim()) ? "" : prefix + stringToReturn;
        }
    }
}

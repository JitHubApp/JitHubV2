using System;
using System.Globalization;
using JitHub.WinUI.Helpers;
using Microsoft.UI.Xaml.Data;

namespace JitHub.WinUI.Converters
{
    public partial class TimeAgoConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value == null)
            {
                return string.Empty;
            }
            DateTime dateTime;
            if (value is DateTimeOffset dto)
            {
                dateTime = dto.LocalDateTime;
            }
            else if (value is DateTime dt)
            {
                dateTime = dt;
            }
            else if (value is string text && DateTime.TryParse(text, out DateTime parsedDate))
            {
                dateTime = parsedDate;
            }
            else
            {
                return string.Empty;
            }

            string? prefix = parameter as string;
            return ConvertDateToTimeAgoFormat(
                dateTime,
                string.IsNullOrWhiteSpace(prefix)
                    ? L("TimeAgo/DefaultPrefix", "Updated ")
                    : $"{prefix} ");
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();

        private static string ConvertDateToTimeAgoFormat(DateTime dt, string prefix)
        {
            TimeSpan ts = DateTime.Now - dt;
            double delta = Math.Abs(ts.TotalSeconds);

            string stringToReturn;
            if (delta < 60)
            {
                int seconds = Math.Abs(ts.Seconds);
                if (seconds == 1)
                {
                    stringToReturn = L("aSecondAgo", "one second ago");
                }
                else
                {
                    stringToReturn = FormatCount(seconds, L("secondsAgo", "seconds ago"));
                }
            }
            else if (delta < 120)
            {
                stringToReturn = L("aMinuteAgo", "a minute ago");
            }
            else if (delta < 2700) // 45 * 60
            {
                stringToReturn = FormatCount(Math.Abs(ts.Minutes), L("minutesAgo", "minutes ago"));
            }
            else if (delta < 5400) // 90 * 60
            {
                stringToReturn = L("anHourAgo", "an hour ago");
            }
            else if (delta < 86400) // 24 * 60 * 60
            {
                stringToReturn = FormatCount(Math.Abs(ts.Hours), L("hoursAgo", "hours ago"));
            }
            else if (delta < 172800) // 48 * 60 * 60
            {
                stringToReturn = L("aDayAgo", "a day ago");
            }
            else if (delta < 2592000) // 30 * 24 * 60 * 60
            {
                stringToReturn = FormatCount(Math.Abs(ts.Days), L("daysAgo", "days ago"));
            }
            else if (delta < 31104000) // 12 * 30 * 24 * 60 * 60
            {
                int months = System.Convert.ToInt32(Math.Floor(Math.Abs((double)ts.Days) / 30));

                if (months <= 1)
                {
                    stringToReturn = L("oneMonthAgo", "one month ago");
                }
                else
                {
                    stringToReturn = FormatCount(months, L("monthsAgo", "months ago"));
                }
            }
            else
            {
                int years = System.Convert.ToInt32(Math.Floor(Math.Abs((double)ts.Days) / 365));

                if (years <= 1)
                {
                    stringToReturn = L("oneYearAgo", "one year ago");
                }
                else
                {
                    stringToReturn = FormatCount(years, L("yearsAgo", "years ago"));
                }
            }

            return string.IsNullOrWhiteSpace(stringToReturn) ? string.Empty : prefix + stringToReturn;
        }

        private static string FormatCount(int count, string unit) =>
            string.Format(CultureInfo.CurrentCulture, "{0} {1}", count, unit);

        private static string L(string key, string fallback) =>
            LocalizedResourceText.GetString(key, fallback);
    }
}



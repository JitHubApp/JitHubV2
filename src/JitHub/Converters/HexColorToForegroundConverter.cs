using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.UI;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Media;

namespace JitHub.Converters
{
    class HexColorToForegroundConverter : IValueConverter
    {
		public object Convert(object value, Type targetType, object parameter, string language)
		{
			SolidColorBrush background = GetSolidColorBrush((value as string) + "FF");
			return new SolidColorBrush(PerceivedBrightness(background) > 130 ? Colors.Black : Colors.White);
		}

		public object ConvertBack(object value, Type targetType, object parameter, string language)
			=> throw new NotImplementedException();

		public static SolidColorBrush GetSolidColorBrush(string hex)
		{
			var r = (byte)System.Convert.ToUInt32(hex.Substring(0, 2), 16);
			var g = (byte)System.Convert.ToUInt32(hex.Substring(2, 2), 16);
			var b = (byte)System.Convert.ToUInt32(hex.Substring(4, 2), 16);
			var a = (byte)System.Convert.ToUInt32(hex.Substring(6, 2), 16);
			var myBrush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
			return myBrush;
		}

		private int PerceivedBrightness(SolidColorBrush c)
			=> (int)Math.Sqrt(
					c.Color.R * c.Color.R * .299 +
					c.Color.G * c.Color.G * .587 +
					c.Color.B * c.Color.B * .114);
	}
}

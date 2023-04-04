using Octokit;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Windows.UI;
using Windows.UI.Xaml.Controls;

namespace JitHub.Helpers
{
    public static class Extensions
    {
        public static Color HexToColor(this string hexColor)
        {
            byte a = byte.Parse(hexColor.Substring(1, 2), NumberStyles.HexNumber);
            byte r = byte.Parse(hexColor.Substring(3, 2), NumberStyles.HexNumber);
            byte g = byte.Parse(hexColor.Substring(5, 2), NumberStyles.HexNumber);
            byte b = byte.Parse(hexColor.Substring(7, 2), NumberStyles.HexNumber);

            Color color = Color.FromArgb(a, r, g, b);
            return color;
        }

        public static async void HandleResize(this WebView webview)
        {
            try
            {
                var heightString = await webview.InvokeScriptAsync("eval", new[] { "document.getElementById('container').scrollHeight.toString()" });
                int height;
                if (int.TryParse(heightString, out height))
                {
                    webview.Height = height;
                }
            }
            catch (Exception e)
            {

            }
        }

        public static string GetRepositoryFullName(this Repository repo)
        {
            return string.IsNullOrWhiteSpace(repo?.FullName) ? repo?.Name : repo?.FullName;
        }

        public static string NormalizeString(this string text)
        {
            return text.Replace('\r', '\n');
        }

        public static ReactionType ToReactionType(this string type)
        {
            switch (type)
            {
                case "Plus1":
                    return ReactionType.Plus1;
                case "Minus1":
                    return ReactionType.Minus1;
                case "Laugh":
                    return ReactionType.Laugh;
                case "Confused":
                    return ReactionType.Confused;
                case "Heart":
                    return ReactionType.Heart;
                case "Hooray":
                    return ReactionType.Hooray;
                case "Rocket":
                    return ReactionType.Rocket;
                case "Eyes":
                    return ReactionType.Eyes;
                default:
                    return ReactionType.Plus1;
            }
        }

        public static (double height, double width) GetSvgSize(string svgString)
        {
            // Parse the SVG string as an XML document
            XDocument svgDocument = XDocument.Parse(svgString);

            // Get the root element of the document
            XElement svgElement = svgDocument.Root;

            // Get the height and width attributes of the root element
            XAttribute heightAttribute = svgElement.Attribute("height");
            XAttribute widthAttribute = svgElement.Attribute("width");

            // Convert the attribute values to double
            double.TryParse(heightAttribute?.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out double height);
            double.TryParse(widthAttribute?.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out double width);

            // Return the height and width as a tuple
            return (height, width);
        }
    }
}

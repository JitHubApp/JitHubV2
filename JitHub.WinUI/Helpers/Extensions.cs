using JitHub.Models.LegacyGitHub;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using Windows.UI;

namespace JitHub.WinUI.Helpers
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

        public static string GetRepositoryFullName(this Repository repo)
        {
            return string.IsNullOrWhiteSpace(repo?.FullName)
                ? repo?.Name ?? string.Empty
                : repo.FullName;
        }

        public static string NormalizeString(this string text)
        {
            return (text ?? string.Empty).Replace('\r', '\n');
        }

        public static ReactionType ToReactionType(this string type)
        {
            switch (type?.Trim())
            {
                case "+1":
                case "Plus1":
                case "plus1":
                    return ReactionType.Plus1;
                case "-1":
                case "Minus1":
                case "minus1":
                    return ReactionType.Minus1;
                case "laugh":
                case "Laugh":
                    return ReactionType.Laugh;
                case "confused":
                case "Confused":
                    return ReactionType.Confused;
                case "heart":
                case "Heart":
                    return ReactionType.Heart;
                case "hooray":
                case "Hooray":
                    return ReactionType.Hooray;
                case "rocket":
                case "Rocket":
                    return ReactionType.Rocket;
                case "eyes":
                case "Eyes":
                    return ReactionType.Eyes;
                default:
                    return ReactionType.Plus1;
            }
        }

        public static string ToGitHubReactionContent(this ReactionType type)
        {
            switch (type)
            {
                case ReactionType.Plus1:
                    return "+1";
                case ReactionType.Minus1:
                    return "-1";
                case ReactionType.Laugh:
                    return "laugh";
                case ReactionType.Confused:
                    return "confused";
                case ReactionType.Heart:
                    return "heart";
                case ReactionType.Hooray:
                    return "hooray";
                case ReactionType.Rocket:
                    return "rocket";
                case ReactionType.Eyes:
                    return "eyes";
                default:
                    return "+1";
            }
        }

        public static (double height, double width) GetSvgSize(string svgString)
        {
            // Parse the SVG string as an XML document
            XDocument svgDocument = XDocument.Parse(svgString);

            // Get the root element of the document
            XElement? svgElement = svgDocument.Root;
            if (svgElement is null)
            {
                return (0, 0);
            }

            // Get the height and width attributes of the root element
            XAttribute? heightAttribute = svgElement.Attribute("height");
            XAttribute? widthAttribute = svgElement.Attribute("width");

            // Convert the attribute values to double
            double.TryParse(heightAttribute?.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out double height);
            double.TryParse(widthAttribute?.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out double width);

            // Return the height and width as a tuple
            return (height, width);
        }
    }
}


using System.Globalization;
using System.Text;
using Windows.Foundation;

namespace MarkdownRenderer.Sample;

/// <summary>
/// The JSON value writers the touch observation shares. The automation driver
/// parses this output, so numbers are round-trip invariant and a non-finite
/// value is written as <c>null</c>.
/// </summary>
internal static class TouchContactJson
{
    internal static void AppendPoint(StringBuilder json, Point point)
    {
        json.Append("{\"x\":");
        AppendNumber(json, point.X);
        json.Append(",\"y\":");
        AppendNumber(json, point.Y);
        json.Append('}');
    }

    internal static void AppendRect(StringBuilder json, Rect rect)
    {
        json.Append("{\"x\":");
        AppendNumber(json, rect.X);
        json.Append(",\"y\":");
        AppendNumber(json, rect.Y);
        json.Append(",\"width\":");
        AppendNumber(json, rect.Width);
        json.Append(",\"height\":");
        AppendNumber(json, rect.Height);
        json.Append('}');
    }

    internal static void AppendNumber(StringBuilder json, double value)
    {
        if (!double.IsFinite(value))
        {
            json.Append("null");
            return;
        }

        json.Append(value.ToString("R", CultureInfo.InvariantCulture));
    }

    internal static void AppendString(StringBuilder json, string value)
    {
        json.Append('"');
        foreach (char character in value)
        {
            if (character is '"' or '\\')
                json.Append('\\');
            json.Append(character);
        }
        json.Append('"');
    }
}

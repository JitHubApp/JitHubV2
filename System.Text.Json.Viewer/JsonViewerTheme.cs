using Windows.UI;

namespace System.Text.Json.Viewer;

public class JsonViewerTheme
{
    public Color Background { get; set; }
    public Color Key { get; set; }
    public Color String { get; set; }
    public Color Number { get; set; }
    public Color Boolean { get; set; }
    public Color CurlyBracket { get; set; }
    public Color SquareBracket { get; set; }
    public Color Null { get; set; }
    public Color Button { get; set; }
    public Color VerticalLine { get; set; }

    public JsonViewerTheme(
        Color background,
        Color key,
        Color @string,
        Color number,
        Color boolean,
        Color curlyBracket,
        Color squareBracket,
        Color @null,
        Color button,
        Color verticalLine)
    {
        Background = background;
        Key = key;
        String = @string;
        Number = number;
        Boolean = boolean;
        CurlyBracket = curlyBracket;
        SquareBracket = squareBracket;
        Null = @null;
        Button = button;
        VerticalLine = verticalLine;
    }

    public static JsonViewerTheme CreateDefaultTheme()
    {
        return new JsonViewerTheme(
            Colors.Transparent,
            Colors.Purple,
            Colors.Orange,
            Colors.LightBlue,
            Colors.LightBlue,
            Colors.White,
            Colors.White,
            Colors.White,
            Colors.Gray,
            Colors.Gray);
    }
}

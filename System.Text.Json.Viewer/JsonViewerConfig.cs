using Windows.UI.Xaml;

namespace System.Text.Json.Viewer;

public class JsonViewerConfig
{
    public string Json { get; set; }
    public bool EnableCopy { get; set; }
    public bool DisplayVerticalLine { get; set; }
    public bool EnableColapse { get; set; }
    public Visibility ShowCopy { get; set; }
    public JsonViewerTheme ThemeConfig { get; set; }
    
    public JsonViewerConfig(
        string json,
        bool enableCopy,
        bool displayVerticalLine,
        bool enableColapse,
        JsonViewerTheme theme = null)
    {
        Json = json;
        EnableCopy = enableCopy;
        ShowCopy = EnableCopy ? Visibility.Visible : Visibility.Collapsed;
        DisplayVerticalLine = displayVerticalLine;
        EnableColapse = enableColapse;
        ThemeConfig = theme ?? JsonViewerTheme.CreateDefaultTheme();
    }
}

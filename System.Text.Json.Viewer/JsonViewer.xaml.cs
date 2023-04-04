using System.Linq;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace System.Text.Json.Viewer;

public sealed partial class JsonViewer : UserControl
{
    private const int _tabSize = 24;
    private const int _buttonSize = 16;

    public static DependencyProperty ConfigProperty = DependencyProperty.Register(
        nameof(Json),
        typeof(JsonViewerConfig),
        typeof(JsonViewer),
        new PropertyMetadata(default(string), OnJsonChange));

    public static void OnJsonChange(DependencyObject d, DependencyPropertyChangedEventArgs args)
    {
        if (d is JsonViewer self && args.NewValue != null)
        {
            self.Update(self.Config);
        }
    }

    public JsonViewerConfig Config
    {
        get => (JsonViewerConfig)GetValue(ConfigProperty);
        set => SetValue(ConfigProperty, value);
    }

    public JsonViewer()
    {
        this.InitializeComponent();
    }
    
    public JsonViewer(JsonViewerConfig config)
    {
        this.InitializeComponent();
        Config = config;
        Update(config);
    }

    public void Update(JsonViewerConfig config)
    {
        OuterBox.Background = new SolidColorBrush(config.ThemeConfig.Background);
        Container.Children.Clear();
        try
        {
            var jsonDoc = JsonDocument.Parse(config.Json);
            Render(Container, jsonDoc.RootElement, 1);
        }
        catch (Exception ex)
        {
            Container.Children.Add(Text(ex.Message));
        }
    }

    public void Render(StackPanel container, JsonElement jsonElement, int depth)
    {
        var padding = new Thickness(depth * _tabSize, 0, 0, 0);
        switch (jsonElement.ValueKind)
        {
            case JsonValueKind.Object:
                RenderObject(container, jsonElement, depth);
                break;
            case JsonValueKind.Array:
                RenderArray(container, jsonElement, depth);
                break;
            case JsonValueKind.String:
                var stringValue = Text($"\"{jsonElement}\"");
                stringValue.Foreground = new SolidColorBrush(Config.ThemeConfig.String);
                var lastChildString = container.Children.LastOrDefault();
                if (lastChildString != null && lastChildString is StackPanel StringKeyContainer && StringKeyContainer.Children.Count < 2)
                {
                    StringKeyContainer.Children.Add(stringValue);
                }
                else
                {
                    stringValue.Padding = padding;
                    container.Children.Add(stringValue);
                }
                break;
            case JsonValueKind.Number:
                var numberValue = Text($"{jsonElement}");
                numberValue.Foreground = new SolidColorBrush(Config.ThemeConfig.Number);
                var lastChildNumber = container.Children.LastOrDefault();
                if (lastChildNumber != null && lastChildNumber is StackPanel NumberKeyContainer && NumberKeyContainer.Children.Count < 2)
                {
                    NumberKeyContainer.Children.Add(numberValue);
                }
                else
                {
                    numberValue.Padding = padding;
                    container.Children.Add(numberValue);
                }
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                var boolValue = Text($"{jsonElement}");
                boolValue.Foreground = new SolidColorBrush(Config.ThemeConfig.Boolean);
                var lastChildBoolean = container.Children.LastOrDefault();
                if (lastChildBoolean != null && lastChildBoolean is StackPanel BooleanKeyContainer && BooleanKeyContainer.Children.Count < 2)
                {
                    BooleanKeyContainer.Children.Add(boolValue);
                }
                else
                {
                    boolValue.Padding = padding;
                    container.Children.Add(boolValue);
                }
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
            default:
                var lastChildNull = container.Children.LastOrDefault();
                var nullValue = Text("null");
                nullValue.Foreground = new SolidColorBrush(Config.ThemeConfig.Null);
                if (lastChildNull != null && lastChildNull is StackPanel NullKeyContainer && NullKeyContainer.Children.Count < 2)
                {
                    NullKeyContainer.Children.Add(nullValue);
                }
                else
                {
                    nullValue.Padding = padding;
                    container.Children.Add(nullValue);
                }
                break;
        }
    }

    private void RenderObject(StackPanel container, JsonElement jsonElement, int depth)
    {
        var padding = new Thickness(depth * _tabSize, 0, 0, 0);
        var lineMargin = new Thickness(depth * _tabSize, _buttonSize, 0, _buttonSize);
        var paddingWithButton = new Thickness((depth - 1) * _tabSize, 0, 0, 0);
        var keyPadding = new Thickness((depth + 1) * _tabSize, 0, 0, 0);
        var keyWithButtonPadding = new Thickness((depth * _tabSize) - _tabSize - 4, 0, 0, 0);

        var objectGrid = new Grid();
        var lineGrid = new Grid();
        if (Config.DisplayVerticalLine)
        {
            lineGrid.BorderBrush = new SolidColorBrush(Config.ThemeConfig.VerticalLine);
            lineGrid.BorderThickness = new Thickness(1, 0, 0, 0);
            lineGrid.Width = 2;
            lineGrid.VerticalAlignment = VerticalAlignment.Stretch;
            lineGrid.HorizontalAlignment = HorizontalAlignment.Left;
            lineGrid.Margin = lineMargin;
            lineGrid.Opacity = 0.5;
            objectGrid.Children.Add(lineGrid);
        }
        var dotdotdot = Text("...");
        dotdotdot.Padding = keyPadding;
        dotdotdot.Margin = new Thickness(0);
        dotdotdot.Visibility = Visibility.Collapsed;
        objectGrid.Children.Add(dotdotdot);
        var objContainer = new StackPanel();
        objContainer.Spacing = 2;
        Button button = ToggleButton(dotdotdot, objContainer);
        
        var openingCurlyBracket = Text("{");
        openingCurlyBracket.Foreground = new SolidColorBrush(Config.ThemeConfig.CurlyBracket);
        var lastKeyCurl = container.Children.LastOrDefault();
        if (lastKeyCurl != null && lastKeyCurl is StackPanel CurlyKeyContainer && CurlyKeyContainer.Children.Count < 2)
        {
            CurlyKeyContainer.Children.Add(openingCurlyBracket);
            CurlyKeyContainer.Children.Insert(0, button);
            CurlyKeyContainer.Padding = keyWithButtonPadding;
        }
        else
        {
            var curlContainerWithButton = new StackPanel();
            curlContainerWithButton.Orientation = Orientation.Horizontal;
            curlContainerWithButton.Children.Add(button);
            curlContainerWithButton.Children.Add(openingCurlyBracket);
            curlContainerWithButton.Padding = paddingWithButton;
            container.Children.Add(curlContainerWithButton);
        }

        foreach (var prop in jsonElement.EnumerateObject())
        {
            var keyContainer = new StackPanel();
            keyContainer.Spacing = 4;
            keyContainer.Padding = keyPadding;
            keyContainer.Orientation = Orientation.Horizontal;
            var key = Text($"\"{prop.Name}\":");
            key.Foreground = new SolidColorBrush(Config.ThemeConfig.Key);
            keyContainer.Children.Add(key);
            objContainer.Children.Add(keyContainer);
            Render(objContainer, prop.Value, depth + 1);
        }
        objectGrid.Children.Add(objContainer);
        container.Children.Add(objectGrid);
        var closingCurlyBracket = Text("}");
        closingCurlyBracket.Foreground = new SolidColorBrush(Config.ThemeConfig.CurlyBracket);
        closingCurlyBracket.Padding = padding;
        container.Children.Add(closingCurlyBracket);
    }

    private void RenderArray(StackPanel container, JsonElement jsonElement, int depth)
    {
        var padding = new Thickness(depth * _tabSize, 0, 0, 0);
        var paddingWithButton = new Thickness((depth - 1) * _tabSize, 0, 0, 0);
        var keyPadding = new Thickness((depth + 1) * _tabSize, 0, 0, 0);
        var keyWithButtonPadding = new Thickness((depth * _tabSize) - 24, 0, 0, 0);

        var objectGrid = new Grid();
        var dotdotdot = Text("...");
        dotdotdot.Padding = keyPadding;
        dotdotdot.Visibility = Visibility.Collapsed;
        objectGrid.Children.Add(dotdotdot);
        var objContainer = new StackPanel();
        objContainer.Spacing = 2;
        Button button = ToggleButton(dotdotdot, objContainer);
        var openingCurlyBracket = Text("[");
        openingCurlyBracket.Foreground = new SolidColorBrush(Config.ThemeConfig.CurlyBracket);
        var lastKeyCurl = container.Children.LastOrDefault();
        if (lastKeyCurl != null && lastKeyCurl is StackPanel CurlyKeyContainer && CurlyKeyContainer.Children.Count < 2)
        {
            CurlyKeyContainer.Children.Add(openingCurlyBracket);
            if (Config.EnableColapse)
            {
                CurlyKeyContainer.Children.Insert(0, button);
            }
            CurlyKeyContainer.Padding = keyWithButtonPadding;
        }
        else
        {
            var curlContainerWithButton = new StackPanel();
            curlContainerWithButton.Orientation = Orientation.Horizontal;
            if (Config.EnableColapse)
            {
                curlContainerWithButton.Children.Add(button);
            }
            curlContainerWithButton.Children.Add(openingCurlyBracket);
            curlContainerWithButton.Padding = paddingWithButton;
            container.Children.Add(curlContainerWithButton);
        }
        foreach (var item in jsonElement.EnumerateArray())
        {
            Render(objContainer, item, depth + 1);
        }
        objectGrid.Children.Add(objContainer);
        container.Children.Add(objectGrid);
        var closingCurlyBracket = Text("]");
        closingCurlyBracket.Foreground = new SolidColorBrush(Config.ThemeConfig.CurlyBracket);
        closingCurlyBracket.Padding = padding;
        container.Children.Add(closingCurlyBracket);
    }

    private Button ToggleButton(TextBlock dotdotdot, StackPanel objContainer)
    {
        var button = new Button();
        button.Background = new SolidColorBrush(Colors.Transparent);
        var buttonBrush = new SolidColorBrush(Config.ThemeConfig.Button);
        button.BorderBrush = buttonBrush;
        button.BorderThickness = new Thickness(1);
        var label = Label("-");
        label.Foreground = buttonBrush;
        var translate = new TranslateTransform
        {
            X = 0,
            Y = -4
        };
        var group = new TransformGroup
        {
            Children =
            {
              translate
            }
        };
        label.RenderTransform = group;
        button.Content = label;
        button.Click += (sender, args) =>
        {
            if (((Button)sender).Content != null && ((Button)sender).Content is TextBlock label && label.Text == "-")
            {
                objContainer.Visibility = Visibility.Collapsed;
                dotdotdot.Visibility = Visibility.Visible;
                var plusLabel = Label("+");
                plusLabel.Foreground = buttonBrush;
                plusLabel.RenderTransform = group;
                ((Button)sender).Content = plusLabel;
            }
            else
            {
                objContainer.Visibility = Visibility.Visible;
                dotdotdot.Visibility = Visibility.Collapsed;
                var minusLabel = Label("-");
                minusLabel.Foreground = buttonBrush;
                minusLabel.RenderTransform = group;
                ((Button)sender).Content = minusLabel;
            }
        };
        button.Width = _buttonSize;
        button.Height = _buttonSize;
        button.Margin = new Thickness(2);
        button.HorizontalContentAlignment = HorizontalAlignment.Center;
        button.VerticalContentAlignment = VerticalAlignment.Top;
        button.Padding = new Thickness(0);
        button.CornerRadius = new CornerRadius(4);
        button.VerticalAlignment = VerticalAlignment.Top;
        button.HorizontalAlignment = HorizontalAlignment.Left;
        return button;
    }

    private TextBlock Text(string text)
    {
        return new TextBlock()
        {
            Text = text,
            IsTextSelectionEnabled = true,
        };
    }

    private TextBlock Label(string text)
    {
        return new TextBlock()
        {
            Text = text,
        };
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (Config.EnableCopy)
        {
            var dataPackage = new DataPackage();
            dataPackage.SetText(Config.Json);
            Clipboard.SetContent(dataPackage);
        }
    }
}

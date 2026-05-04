using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace JitHub.WinUI.Views.Controls.App;

public sealed partial class AppStatButton : UserControl
{
    public static readonly DependencyProperty IconGlyphProperty = DependencyProperty.Register(nameof(IconGlyph), typeof(string), typeof(AppStatButton), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty IconKindProperty = DependencyProperty.Register(nameof(IconKind), typeof(AppIconKind), typeof(AppStatButton), new PropertyMetadata(AppIconKind.Star));
    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.Register(nameof(IsSelected), typeof(bool), typeof(AppStatButton), new PropertyMetadata(false));
    public static readonly DependencyProperty ValueTextProperty = DependencyProperty.Register(nameof(ValueText), typeof(string), typeof(AppStatButton), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty IconForegroundProperty = DependencyProperty.Register(nameof(IconForeground), typeof(Brush), typeof(AppStatButton), new PropertyMetadata(null));
    public static readonly DependencyProperty SelectedIconForegroundProperty = DependencyProperty.Register(nameof(SelectedIconForeground), typeof(Brush), typeof(AppStatButton), new PropertyMetadata(null));
    public static readonly DependencyProperty CommandProperty = DependencyProperty.Register(nameof(Command), typeof(ICommand), typeof(AppStatButton), new PropertyMetadata(null));
    public static readonly DependencyProperty AutomationIdProperty = DependencyProperty.Register(nameof(AutomationId), typeof(string), typeof(AppStatButton), new PropertyMetadata(string.Empty));

    public AppStatButton()
    {
        InitializeComponent();
    }

    public string IconGlyph
    {
        get => (string)GetValue(IconGlyphProperty);
        set => SetValue(IconGlyphProperty, value);
    }

    public AppIconKind IconKind
    {
        get => (AppIconKind)GetValue(IconKindProperty);
        set => SetValue(IconKindProperty, value);
    }

    public bool IsSelected
    {
        get => (bool)GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    public string ValueText
    {
        get => (string)GetValue(ValueTextProperty);
        set => SetValue(ValueTextProperty, value);
    }

    public Brush? IconForeground
    {
        get => (Brush?)GetValue(IconForegroundProperty);
        set => SetValue(IconForegroundProperty, value);
    }

    public Brush? SelectedIconForeground
    {
        get => (Brush?)GetValue(SelectedIconForegroundProperty);
        set => SetValue(SelectedIconForegroundProperty, value);
    }

    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public string AutomationId
    {
        get => (string)GetValue(AutomationIdProperty);
        set => SetValue(AutomationIdProperty, value);
    }
}

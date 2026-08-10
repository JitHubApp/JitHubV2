using System.Threading;
using System.Windows.Input;
using JitHub.WinUI.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace JitHub.WinUI.Views.Controls.App;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class AppStatButton : UserControl
{
    private static long _nextFallbackAutomationIdentity;
    private readonly string _fallbackAutomationId = AutomationIdentity.CreateScopedId(
        "AppStatButton",
        $"runtime:{Interlocked.Increment(ref _nextFallbackAutomationIdentity)}");

    public static readonly DependencyProperty IconGlyphProperty = DependencyProperty.Register(nameof(IconGlyph), typeof(string), typeof(AppStatButton), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty IconKindProperty = DependencyProperty.Register(nameof(IconKind), typeof(AppIconKind), typeof(AppStatButton), new PropertyMetadata(AppIconKind.Star, OnAutomationPresentationChanged));
    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.Register(nameof(IsSelected), typeof(bool), typeof(AppStatButton), new PropertyMetadata(false));
    public static readonly DependencyProperty ValueTextProperty = DependencyProperty.Register(nameof(ValueText), typeof(string), typeof(AppStatButton), new PropertyMetadata(string.Empty, OnAutomationPresentationChanged));
    public static readonly DependencyProperty IconForegroundProperty = DependencyProperty.Register(nameof(IconForeground), typeof(Brush), typeof(AppStatButton), new PropertyMetadata(null));
    public static readonly DependencyProperty SelectedIconForegroundProperty = DependencyProperty.Register(nameof(SelectedIconForeground), typeof(Brush), typeof(AppStatButton), new PropertyMetadata(null));
    public static readonly DependencyProperty CommandProperty = DependencyProperty.Register(nameof(Command), typeof(ICommand), typeof(AppStatButton), new PropertyMetadata(null));
    public static readonly DependencyProperty AutomationIdProperty = DependencyProperty.Register(nameof(AutomationId), typeof(string), typeof(AppStatButton), new PropertyMetadata(null, OnAutomationPresentationChanged));
    public static readonly DependencyProperty AutomationNameProperty = DependencyProperty.Register(nameof(AutomationName), typeof(string), typeof(AppStatButton), new PropertyMetadata(null, OnAutomationPresentationChanged));
    public static readonly DependencyProperty IsActionEnabledProperty = DependencyProperty.Register(nameof(IsActionEnabled), typeof(bool), typeof(AppStatButton), new PropertyMetadata(true));

    public AppStatButton()
    {
        InitializeComponent();
    }

    public string EffectiveAutomationId => string.IsNullOrWhiteSpace(AutomationId)
        ? _fallbackAutomationId
        : AutomationId.Trim();

    public string EffectiveAutomationName => string.IsNullOrWhiteSpace(AutomationName)
        ? $"{IconKind} repository statistic {ValueText}".Trim()
        : AutomationName.Trim();

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

    public string? AutomationId
    {
        get => (string?)GetValue(AutomationIdProperty);
        set => SetValue(AutomationIdProperty, value);
    }

    public string? AutomationName
    {
        get => (string?)GetValue(AutomationNameProperty);
        set => SetValue(AutomationNameProperty, value);
    }

    public bool IsActionEnabled
    {
        get => (bool)GetValue(IsActionEnabledProperty);
        set => SetValue(IsActionEnabledProperty, value);
    }

    private static void OnAutomationPresentationChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AppStatButton button)
        {
            button.Bindings.Update();
        }
    }

}

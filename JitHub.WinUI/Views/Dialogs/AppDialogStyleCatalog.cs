using System;
using JitHub.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.WinUI.Views.Dialogs;

internal static class AppDialogStyleCatalog
{
    public static void Apply(ContentDialog dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);

        dialog.Style = GetStyle("AppContentDialogStyle");
        object? content = dialog.Content;
        dialog.Content = null;
        dialog.Content = NormalizeContent(content);
        ApplyFieldStyles(dialog.Content as DependencyObject);
    }

    public static void ApplyLayout(
        ContentDialog dialog,
        XamlRoot xamlRoot,
        AppDialogLayoutKind layoutKind = AppDialogLayoutKind.Standard)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        ArgumentNullException.ThrowIfNull(xamlRoot);

        DialogLayoutMetrics metrics = DialogLayoutPolicy.Calculate(
            xamlRoot.Size.Width,
            xamlRoot.Size.Height,
            layoutKind);
        dialog.Resources["ContentDialogMinWidth"] = metrics.MinimumWidth;
        dialog.Resources["ContentDialogMaxWidth"] = metrics.MaximumWidth;
        dialog.Resources["ContentDialogMaxHeight"] = metrics.MaximumHeight;
        dialog.MinWidth = 0;
        dialog.MaxWidth = metrics.MaximumWidth;
        dialog.MinWidth = metrics.MinimumWidth;
        dialog.MaxHeight = metrics.MaximumHeight;
        dialog.InvalidateMeasure();
    }

    public static StackPanel CreateContentPanel(params UIElement[] children)
    {
        StackPanel panel = new()
        {
            Style = GetStyle("AppDialogContentStyle")
        };

        foreach (UIElement child in children)
        {
            panel.Children.Add(child);
        }

        return panel;
    }

    private static object? NormalizeContent(object? content)
    {
        if (content is AppDialogScrollableContent)
        {
            return content;
        }

        if (content is ScrollViewer scrollViewer)
        {
            return scrollViewer;
        }

        if (content is StackPanel panel)
        {
            panel.Style = GetStyle("AppDialogContentStyle");
            return CreateScrollableContent(panel);
        }

        if (content is UIElement element)
        {
            return CreateScrollableContent(CreateContentPanel(element));
        }

        if (content is null)
        {
            return CreateScrollableContent(CreateContentPanel());
        }

        TextBlock text = new()
        {
            Text = content.ToString() ?? string.Empty,
            TextWrapping = TextWrapping.Wrap
        };
        return CreateScrollableContent(CreateContentPanel(text));
    }

    private static ScrollViewer CreateScrollableContent(UIElement content) => new()
    {
        Content = content,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        HorizontalScrollMode = ScrollMode.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollMode = ScrollMode.Auto,
        ZoomMode = ZoomMode.Disabled
    };

    private static void ApplyFieldStyles(DependencyObject? element)
    {
        if (element is null)
        {
            return;
        }

        if (element is TextBox { Style: null } textBox)
        {
            textBox.Style = GetStyle("AppTextBoxStyle");
        }
        else if (element is ComboBox { Style: null } comboBox)
        {
            comboBox.Style = GetStyle("AppCompactComboBoxStyle");
        }

        switch (element)
        {
            case Panel panel:
                foreach (UIElement child in panel.Children)
                {
                    ApplyFieldStyles(child);
                }
                break;
            case Border border:
                ApplyFieldStyles(border.Child);
                break;
            case ContentControl contentControl when contentControl.Content is DependencyObject child:
                ApplyFieldStyles(child);
                break;
        }
    }

    private static Style GetStyle(string resourceKey) =>
        Application.Current.Resources.TryGetValue(resourceKey, out object? resource)
            && resource is Style style
                ? style
                : throw new InvalidOperationException($"Required dialog style '{resourceKey}' is unavailable.");
}

/// <summary>
/// Marks dialog content that already owns its vertical scrolling. The dialog
/// catalog leaves this layout unwrapped so there is only one scroll owner.
/// </summary>
internal sealed class AppDialogScrollableContent : Grid
{
}

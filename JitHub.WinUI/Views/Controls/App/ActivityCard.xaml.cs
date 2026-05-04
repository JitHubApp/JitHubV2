using System.Collections.Generic;
using JitHub.Models.Activities;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace JitHub.WinUI.Views.Controls.App;

public sealed partial class ActivityCard : UserControl
{
    public ActivityCard()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        RenderSentence();
    }

    private void RenderSentence()
    {
        if (SentenceRichTextBlock is null)
        {
            return;
        }

        SentenceRichTextBlock.Blocks.Clear();
        if (DataContext is not ActivityCardViewModel activity)
        {
            return;
        }

        var paragraph = new Paragraph();
        IReadOnlyList<ActivitySentencePartViewModel> parts = activity.SentenceParts.Count > 0
            ? activity.SentenceParts
            : [new ActivitySentencePartViewModel { Text = activity.Title, IsEmphasized = true }];

        foreach (ActivitySentencePartViewModel part in parts)
        {
            if (string.IsNullOrEmpty(part.Text))
            {
                continue;
            }

            if (part.IsAction)
            {
                paragraph.Inlines.Add(CreateInlineAction(part));
                continue;
            }

            paragraph.Inlines.Add(new Run
            {
                Text = part.Text,
                FontWeight = part.IsEmphasized ? FontWeights.SemiBold : FontWeights.Normal
            });
        }

        SentenceRichTextBlock.Blocks.Add(paragraph);
    }

    private InlineUIContainer CreateInlineAction(ActivitySentencePartViewModel part)
    {
        var linkText = new TextBlock
        {
            FontFamily = Resource<FontFamily>("AppUiFontFamily"),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = Resource<Brush>("ActivityInlineLinkForegroundBrush"),
            TextDecorations = Windows.UI.Text.TextDecorations.Underline,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, -1, 0, 0)
        };

        linkText.PointerEntered += (_, _) => linkText.Foreground = Resource<Brush>("ActivityInlineLinkForegroundPointerOverBrush");
        linkText.PointerExited += (_, _) => linkText.Foreground = Resource<Brush>("ActivityInlineLinkForegroundBrush");
        linkText.PointerPressed += (_, _) => linkText.Foreground = Resource<Brush>("ActivityInlineLinkForegroundPressedBrush");
        linkText.PointerReleased += (_, _) => linkText.Foreground = Resource<Brush>("ActivityInlineLinkForegroundPointerOverBrush");
        linkText.Tapped += (_, _) => ExecuteInlineAction(part);

        if (!string.IsNullOrWhiteSpace(part.Glyph))
        {
            linkText.Inlines.Add(new Run
            {
                Text = $"{part.Glyph} ",
                FontFamily = Resource<FontFamily>("SegoeFluentIcons"),
                FontSize = 12,
                FontWeight = FontWeights.Normal
            });
        }

        linkText.Inlines.Add(new Run
        {
            Text = part.Text,
            FontFamily = Resource<FontFamily>("AppUiFontFamily"),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
        });

        return new InlineUIContainer
        {
            Child = linkText
        };
    }

    private static void ExecuteInlineAction(ActivitySentencePartViewModel part)
    {
        if (part.Command?.CanExecute(part.Target) == true)
        {
            part.Command.Execute(part.Target);
        }
    }

    private T Resource<T>(string key)
        where T : class
    {
        if (Resources.TryGetValue(key, out object localValue) && localValue is T localTyped)
        {
            return localTyped;
        }

        if (Application.Current.Resources.TryGetValue(key, out object value) && value is T typed)
        {
            return typed;
        }

        return null!;
    }
}

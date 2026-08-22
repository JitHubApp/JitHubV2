using System;
using System.Collections.Generic;
using System.Linq;
using JitHub.Models.Activities;
using JitHub.WinUI.Helpers;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace JitHub.WinUI.Views.Controls.App;

public sealed partial class ActivityCard : UserControl
{
    public ActivityCardViewModel? ViewModel { get; private set; }

    public ActivityCard()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        ViewModel = args.NewValue as ActivityCardViewModel;
        Bindings.Update();
        RenderSentence();
    }

    private void RenderSentence()
    {
        if (SentenceRichTextBlock is null)
        {
            return;
        }

        SentenceRichTextBlock.Blocks.Clear();
        if (ViewModel is not ActivityCardViewModel activity)
        {
            return;
        }

        var paragraph = new Paragraph();
        List<ActivitySentencePartViewModel> parts = activity.SentenceParts.Count > 0
            ? activity.SentenceParts
            :
            [
                new ActivitySentencePartViewModel { Text = activity.Title, IsEmphasized = true }
            ];

        int actionIndex = 0;
        foreach (ActivitySentencePartViewModel part in parts)
        {
            if (string.IsNullOrEmpty(part.Text))
            {
                continue;
            }

            if (part.IsAction)
            {
                paragraph.Inlines.Add(CreateInlineAction(part, actionIndex++));
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

    private Inline CreateInlineAction(ActivitySentencePartViewModel part, int actionIndex)
    {
        var hyperlink = new Hyperlink
        {
            Foreground = Resource<Brush>("ActivityInlineLinkForegroundBrush"),
        };
        AutomationProperties.SetAutomationId(
            hyperlink,
            AutomationIdentity.CreateScopedId(
                "ActivityInlineAction",
                ActivityScope(),
                $"{actionIndex}_{AutomationToken(part.Text)}"));
        AutomationProperties.SetName(hyperlink, part.Text);
        hyperlink.Click += (_, _) => ExecuteInlineAction(part);

        if (!string.IsNullOrWhiteSpace(part.Glyph))
        {
            hyperlink.Inlines.Add(new Run
            {
                Text = $"{part.Glyph} ",
                FontFamily = Resource<FontFamily>("SegoeFluentIcons"),
                FontSize = Resource<double>("AppFontSize12"),
                FontWeight = FontWeights.Normal
            });
        }

        hyperlink.Inlines.Add(new Run
        {
            Text = part.Text,
            FontFamily = Resource<FontFamily>("AppUiFontFamily"),
            FontSize = Resource<double>("AppFontSize15"),
            FontWeight = FontWeights.SemiBold,
        });

        return hyperlink;
    }

    private string ActivityScope() => ViewModel is { } activity
        ? !string.IsNullOrWhiteSpace(activity.EventId)
            ? activity.EventId
            : $"{activity.EventType}|{activity.TimestampText}|{activity.Title}"
        : "unknown";

    private static string AutomationToken(string value)
    {
        string token = string.Concat(value.Where(char.IsLetterOrDigit).Take(40));
        return string.IsNullOrEmpty(token) ? "Action" : token;
    }

    private static void ExecuteInlineAction(ActivitySentencePartViewModel part)
    {
        if (part.Command?.CanExecute(part.Target) == true)
        {
            part.Command.Execute(part.Target);
        }
    }

    private T Resource<T>(string key)
    {
        if (Resources.TryGetValue(key, out object localValue) && localValue is T localTyped)
        {
            return localTyped;
        }

        if (Application.Current.Resources.TryGetValue(key, out object value) && value is T typed)
        {
            return typed;
        }

        return default!;
    }
}

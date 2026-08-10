using System.Collections.Generic;
using System;
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

public sealed partial class ActivitySentenceLine : UserControl
{
    public ActivityCardViewModel? ViewModel { get; private set; }

    public ActivitySentenceLine()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        ViewModel = args.NewValue as ActivityCardViewModel;
        Bindings.Update();
        RenderSentence();
        RenderSecondaryText();
        UpdateSentenceWidth();
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

            paragraph.Inlines.Add(part.IsAction ? CreateInlineAction(part, actionIndex++) : CreateTextRun(part));
        }

        SentenceRichTextBlock.Blocks.Add(paragraph);
    }

    private void RenderSecondaryText()
    {
        if (SecondaryTextBlock is null)
        {
            return;
        }

        string text = SecondaryText();
        SecondaryTextBlock.Text = text;
        SecondaryTextBlock.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private string SecondaryText()
    {
        if (ViewModel is not ActivityCardViewModel activity)
        {
            return string.Empty;
        }

        string sentenceText = NormalizedSentenceText(activity);
        string? detail = activity.Details
            .Select(static item => item.Text)
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)
                && !IsRedundantDetail(sentenceText, text));

        if (!string.IsNullOrWhiteSpace(detail))
        {
            return detail!;
        }

        if (!string.IsNullOrWhiteSpace(activity.Subtitle)
            && !string.Equals(activity.Subtitle, activity.RepoDisplayName, StringComparison.OrdinalIgnoreCase))
        {
            return activity.Subtitle;
        }

        return string.Empty;
    }

    private static string NormalizedSentenceText(ActivityCardViewModel activity)
    {
        IEnumerable<ActivitySentencePartViewModel> parts = activity.SentenceParts.Count > 0
            ? activity.SentenceParts
            : [new ActivitySentencePartViewModel { Text = activity.Title }];

        return NormalizeText(string.Concat(parts.Select(static part => part.Text)));
    }

    private static bool IsRedundantDetail(string normalizedSentence, string detail)
    {
        string normalizedDetail = NormalizeText(detail);
        return normalizedDetail.Length > 0
            && normalizedSentence.Contains(normalizedDetail, StringComparison.Ordinal);
    }

    private static string NormalizeText(string value)
    {
        Span<char> buffer = stackalloc char[value.Length];
        int index = 0;
        foreach (char character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                buffer[index++] = char.ToLowerInvariant(character);
            }
        }

        return new string(buffer[..index]);
    }

    private static Run CreateTextRun(ActivitySentencePartViewModel part) =>
        new()
        {
            Text = part.Text,
            FontWeight = part.IsEmphasized ? FontWeights.SemiBold : FontWeights.Normal
        };

    private Inline CreateInlineAction(ActivitySentencePartViewModel part, int actionIndex)
    {
        var hyperlink = new Hyperlink
        {
            Foreground = Resource<Brush>("ActivityInlineLinkForegroundBrush"),
        };

        AutomationProperties.SetAutomationId(
            hyperlink,
            AutomationIdentity.CreateScopedId(
                "ActivitySentenceInlineAction",
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
                FontSize = 11,
                FontWeight = FontWeights.Normal
            });
        }

        hyperlink.Inlines.Add(new Run
        {
            Text = BreakableInlineText(ActionLabel(part)),
            FontFamily = Resource<FontFamily>("AppUiFontFamily"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold
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

    private static string ActionLabel(ActivitySentencePartViewModel part)
    {
        if (part.Target?.Kind == ActivityNavigationTargetKind.Repository)
        {
            int separator = part.Text.IndexOf('/', StringComparison.Ordinal);
            if (separator >= 0 && separator < part.Text.Length - 1)
            {
                return part.Text[(separator + 1)..];
            }
        }

        return part.Text;
    }

    private static string BreakableInlineText(string text) =>
        text
            .Replace("/", "/\u200B", StringComparison.Ordinal)
            .Replace("-", "-\u200B", StringComparison.Ordinal)
            .Replace("_", "_\u200B", StringComparison.Ordinal);

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

    private void LayoutRoot_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateSentenceWidth();

    private void UpdateSentenceWidth()
    {
        if (SentenceRichTextBlock is null || LayoutRoot is null)
        {
            return;
        }

        double visibleWidth = LayoutRoot.ActualWidth;
        try
        {
            if (LayoutRoot.XamlRoot is { Size.Width: > 0 } xamlRoot)
            {
                UIElement? rootVisual = xamlRoot.Content as UIElement;
                Windows.Foundation.Point origin = rootVisual is null
                    ? LayoutRoot.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0))
                    : LayoutRoot.TransformToVisual(rootVisual).TransformPoint(new Windows.Foundation.Point(0, 0));
                double rootWidth = Math.Max(0, xamlRoot.Size.Width - origin.X);
                visibleWidth = visibleWidth > 0 ? Math.Min(visibleWidth, rootWidth) : rootWidth;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            visibleWidth = LayoutRoot.ActualWidth;
        }

        double available = visibleWidth - 28 - 58 - 20;
        double width = visibleWidth < 760 ? Math.Min(320, available) : available;
        SentenceRichTextBlock.Width = Math.Max(80, width);
    }
}

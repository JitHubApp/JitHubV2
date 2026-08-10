using System;
using System.Collections.Generic;
using JitHub.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace JitHub.WinUI.Views.Controls.Commit;

public static class CommitDiffSearchHighlight
{
    public static readonly DependencyProperty MatchesProperty = DependencyProperty.RegisterAttached(
        "Matches",
        typeof(IReadOnlyList<CommitDiffSearchMatch>),
        typeof(CommitDiffSearchHighlight),
        new PropertyMetadata(null, OnMatchesChanged));

    public static IReadOnlyList<CommitDiffSearchMatch>? GetMatches(TextBlock element) =>
        (IReadOnlyList<CommitDiffSearchMatch>?)element.GetValue(MatchesProperty);

    public static void SetMatches(TextBlock element, IReadOnlyList<CommitDiffSearchMatch>? value) =>
        element.SetValue(MatchesProperty, value);

    private static void OnMatchesChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not TextBlock textBlock)
        {
            return;
        }

        textBlock.TextHighlighters.Clear();
        if (args.NewValue is not IReadOnlyList<CommitDiffSearchMatch> matches || matches.Count == 0)
        {
            return;
        }

        TextHighlighter highlighter = new()
        {
            Background = GetThemeBrush(textBlock, "AppAccentBrush"),
            Foreground = GetThemeBrush(textBlock, "AppAccentForegroundBrush")
        };

        foreach (CommitDiffSearchMatch match in matches)
        {
            highlighter.Ranges.Add(new TextRange(match.StartIndex, match.Length));
        }

        textBlock.TextHighlighters.Add(highlighter);
    }

    private static Brush GetThemeBrush(FrameworkElement element, string resourceKey)
    {
        if (element.Resources.TryGetValue(resourceKey, out object localBrush) &&
            localBrush is Brush localThemeBrush)
        {
            return localThemeBrush;
        }

        if (Application.Current.Resources.TryGetValue(resourceKey, out object appBrush) &&
            appBrush is Brush appThemeBrush)
        {
            return appThemeBrush;
        }

        throw new InvalidOperationException($"Required theme brush '{resourceKey}' is unavailable.");
    }
}

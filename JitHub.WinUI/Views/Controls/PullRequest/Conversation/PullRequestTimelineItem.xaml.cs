using System;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using JitHub.Models.Activities;
using JitHub.Models.NavArgs;
using JitHub.Models.PRConversation;
using JitHub.Services;
using JitHub.WinUI.Helpers;
using JitHub.WinUI.ViewModels.PullRequestViewModels.ConversationViewModels;
using JitHub.WinUI.Views.Controls.Common;
using JitHub.WinUI.Views.Pages;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace JitHub.WinUI.Views.Controls.PullRequest.Conversation;

public sealed partial class PullRequestTimelineItem : UserControl
{
    public static readonly DependencyProperty NodeProperty = DependencyProperty.Register(
        nameof(Node),
        typeof(ConversationNode),
        typeof(PullRequestTimelineItem),
        new PropertyMetadata(default(ConversationNode), OnNodeChanged));

    private readonly NavigationService _navigationService;
    private readonly RelayCommand<ActivityNavigationTarget> _navigateCommand;

    public ConversationNode? Node
    {
        get => (ConversationNode?)GetValue(NodeProperty);
        set => SetValue(NodeProperty, value);
    }

    public PullRequestTimelineItem()
    {
        InitializeComponent();
        _navigationService = Ioc.Default.GetService<NavigationService>()
            ?? throw new InvalidOperationException("NavigationService is not registered.");
        _navigateCommand = new RelayCommand<ActivityNavigationTarget>(Navigate);
    }

    private static void OnNodeChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is PullRequestTimelineItem item)
        {
            item.RenderNode(args.NewValue as ConversationNode);
        }
    }

    private void RenderNode(ConversationNode? node)
    {
        if (node is null)
        {
            TimelineCard.DataContext = null;
            Root.DataContext = null;
            DataContext = null;
            ActorAvatar.Login = string.Empty;
            ActorAvatar.Url = string.Empty;
            IconGlyph.Glyph = string.Empty;
            TimestampTextBlock.Text = string.Empty;
            DetailsItemsControl.ItemsSource = null;
            DetailsBorder.Visibility = Visibility.Collapsed;
            SentenceRichTextBlock.Blocks.Clear();
            return;
        }

        PullRequestTimelineItemViewModel viewModel = PullRequestTimelineItemViewModelFactory.Create(node, _navigateCommand);
        TimelineCard.DataContext = viewModel;
        Root.DataContext = viewModel;
        DataContext = viewModel;
        ApplyViewModel(viewModel);
        RenderSentence(viewModel);
    }

    private void ApplyViewModel(PullRequestTimelineItemViewModel viewModel)
    {
        Brush toneBrush = ToneBrush(viewModel.Tone);
        Brush borderBrush = ToneBrush(viewModel.Tone, border: true);

        ActorAvatar.Login = viewModel.ActorLogin;
        ActorAvatar.Url = viewModel.ActorAvatarUrl ?? string.Empty;
        IconGlyph.Glyph = viewModel.Glyph;
        IconGlyph.Foreground = toneBrush;
        IconBorder.BorderBrush = borderBrush;
        DetailsBorder.BorderBrush = borderBrush;
        TimestampTextBlock.Text = FormatTimestamp(viewModel.CreatedAt);
        DetailsItemsControl.ItemsSource = viewModel.Details;
        DetailsBorder.Visibility = viewModel.HasDetails ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenderSentence(PullRequestTimelineItemViewModel viewModel)
    {
        SentenceRichTextBlock.Blocks.Clear();

        var paragraph = new Paragraph();
        foreach (PullRequestTimelineInlinePartViewModel part in viewModel.SentenceParts)
        {
            switch (part.Kind)
            {
                case PullRequestTimelineInlineKind.Action:
                    paragraph.Inlines.Add(CreateInlineAction(part));
                    break;
                case PullRequestTimelineInlineKind.Label:
                    paragraph.Inlines.Add(CreateInlineLabel(part));
                    break;
                case PullRequestTimelineInlineKind.Strong:
                    paragraph.Inlines.Add(CreateRun(part.Text, FontWeights.SemiBold));
                    break;
                default:
                    paragraph.Inlines.Add(CreateRun(part.Text, FontWeights.Normal));
                    break;
            }
        }

        SentenceRichTextBlock.Blocks.Add(paragraph);
    }

    private static Run CreateRun(string text, Windows.UI.Text.FontWeight weight)
    {
        return new Run
        {
            Text = text,
            FontWeight = weight
        };
    }

    private InlineUIContainer CreateInlineAction(PullRequestTimelineInlinePartViewModel part)
    {
        var linkText = new TextBlock
        {
            FontFamily = Resource<FontFamily>("AppUiFontFamily"),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = Resource<Brush>("PullRequestTimelineInlineLinkForegroundBrush"),
            TextDecorations = Windows.UI.Text.TextDecorations.Underline,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(1, -1, 1, 0)
        };

        AutomationProperties.SetName(linkText, part.Text);
        linkText.PointerEntered += (_, _) => linkText.Foreground = Resource<Brush>("PullRequestTimelineInlineLinkForegroundPointerOverBrush");
        linkText.PointerExited += (_, _) => linkText.Foreground = Resource<Brush>("PullRequestTimelineInlineLinkForegroundBrush");
        linkText.PointerPressed += (_, _) => linkText.Foreground = Resource<Brush>("PullRequestTimelineInlineLinkForegroundPressedBrush");
        linkText.PointerReleased += (_, _) => linkText.Foreground = Resource<Brush>("PullRequestTimelineInlineLinkForegroundPointerOverBrush");
        linkText.Tapped += (_, _) =>
        {
            if (part.Command?.CanExecute(part.Target) == true)
            {
                part.Command.Execute(part.Target);
            }
        };

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
            FontWeight = FontWeights.SemiBold
        });

        return new InlineUIContainer
        {
            Child = linkText
        };
    }

    private static InlineUIContainer CreateInlineLabel(PullRequestTimelineInlinePartViewModel part)
    {
        var label = new RepoLabel
        {
            Label = part.Label,
            Margin = new Thickness(3, 0, 3, -3),
            VerticalAlignment = VerticalAlignment.Center
        };

        return new InlineUIContainer
        {
            Child = label
        };
    }

    private void Navigate(ActivityNavigationTarget? target)
    {
        if (target is null || Node?.Repo is null)
        {
            return;
        }

        if (target.Kind == ActivityNavigationTargetKind.Commit && !string.IsNullOrWhiteSpace(target.Sha))
        {
            _navigationService.NavigateTo(
                Node.Repo.GetRepositoryFullName(),
                typeof(RepoDetailPage),
                new RepoDetailPageArgs(
                    RepoPageType.CommitPage,
                    CommitPageNavArg.CreateWithGitRef(Node.Repo, target.Sha),
                    Node.Repo));
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

    private Brush ToneBrush(ActivityCardTone tone, bool border = false)
    {
        if (Application.Current.Resources.TryGetValue("ActivityToneToBrushConverter", out object converter)
            && converter is IValueConverter toneConverter
            && toneConverter.Convert(tone, typeof(Brush), border ? "Border" : null, string.Empty) is Brush brush)
        {
            return brush;
        }

        string key = tone switch
        {
            ActivityCardTone.Accent => "AppAccentBrush",
            ActivityCardTone.Success => "AppSuccessBrush",
            ActivityCardTone.Warning or ActivityCardTone.Gold => "AppWarmAccentBrush",
            ActivityCardTone.Danger => "AppDangerBrush",
            ActivityCardTone.Purple => "AppAccentBrush",
            _ => border ? "AppOutlineBrush" : "AppInkMutedBrush"
        };

        return Resource<Brush>(key);
    }

    private string FormatTimestamp(DateTimeOffset createdAt)
    {
        if (Application.Current.Resources.TryGetValue("TimeAgoConverter", out object converter)
            && converter is IValueConverter timeAgoConverter
            && timeAgoConverter.Convert(createdAt, typeof(string), string.Empty, string.Empty) is string text
            && !string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        return $"Updated {createdAt.LocalDateTime:g}";
    }
}

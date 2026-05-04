using JitHub.Models.Activities;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.WinUI.Helpers;

public sealed partial class ActivityCardTemplateSelector : DataTemplateSelector
{
    public DataTemplate? PushTemplate { get; set; }

    public DataTemplate? IssueTemplate { get; set; }

    public DataTemplate? PullRequestTemplate { get; set; }

    public DataTemplate? CommitTemplate { get; set; }

    public DataTemplate? RepositoryTemplate { get; set; }

    public DataTemplate? DefaultTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item)
    {
        return SelectActivityTemplate(item) ?? base.SelectTemplateCore(item);
    }

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container)
    {
        return SelectActivityTemplate(item) ?? base.SelectTemplateCore(item, container);
    }

    private DataTemplate? SelectActivityTemplate(object item)
    {
        if (item is not ActivityCardViewModel activity)
        {
            return DefaultTemplate;
        }

        return activity.EventType switch
        {
            "PushEvent" => PushTemplate ?? DefaultTemplate,
            "CommitCommentEvent" => CommitTemplate ?? DefaultTemplate,
            "IssuesEvent" or "IssueCommentEvent" => IssueTemplate ?? DefaultTemplate,
            "PullRequestEvent" or "PullRequestReviewEvent" or "PullRequestReviewCommentEvent" => PullRequestTemplate ?? DefaultTemplate,
            "CreateEvent" or "DeleteEvent" or "ForkEvent" or "MemberEvent" or "PublicEvent" or "WatchEvent" => RepositoryTemplate ?? DefaultTemplate,
            _ => DefaultTemplate
        };
    }
}

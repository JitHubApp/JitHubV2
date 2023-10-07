using JitHub.ViewModels.ActivityViewModels;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace JitHub.Helpers
{
    public class ActivityDataTemplateSelector : DataTemplateSelector
    {
        public DataTemplate DefaultTemplate { get; set; }
        public DataTemplate CommitCommentEventTemplate { get; set; }
        public DataTemplate CreateEventTemplate { get; set; }
        public DataTemplate DeleteEventTemplate { get; set; }
        public DataTemplate ForkEventTemplate { get; set; }
        public DataTemplate GollumEventTemplate { get; set; }
        public DataTemplate IssueCommentEventTemplate { get; set; }
        public DataTemplate IssuesEventTemplate { get; set; }
        public DataTemplate MemberEventTemplate { get; set; }
        public DataTemplate PublicEventTemplate { get; set; }
        public DataTemplate PullRequestEventTemplate { get; set; }
        public DataTemplate PullRequestReviewCommentEventTemplate { get; set; }
        public DataTemplate PushEventTemplate { get; set; }
        public DataTemplate ReleaseEventTemplate { get; set; }
        public DataTemplate SponsorshipEventTemplate { get; set; }
        public DataTemplate WatchEventTemplate { get; set; }
        protected override DataTemplate SelectTemplateCore(object item)
        {
            switch (((ActivityViewModel)item).Type)
            {
                case "CommitCommentEvent":
                    return CommitCommentEventTemplate;
                case "CreateEvent":
                    return CreateEventTemplate;
                case "DeleteEvent":
                    return DeleteEventTemplate;
                case "ForkEvent":
                    return ForkEventTemplate;
                case "GollumEvent":
                    return GollumEventTemplate;
                case "IssueCommentEvent":
                    return IssueCommentEventTemplate;
                case "IssuesEvent":
                    return IssuesEventTemplate;
                case "MemberEvent":
                    return MemberEventTemplate;
                case "PublicEvent":
                    return PublicEventTemplate;
                case "PullRequestEvent":
                    return PullRequestEventTemplate;
                case "PullRequestReviewCommentEvent":
                    return PullRequestReviewCommentEventTemplate;
                case "PushEvent":
                    return PushEventTemplate;
                case "ReleaseEvent":
                    return ReleaseEventTemplate;
                case "SponsorshipEvent":
                    return SponsorshipEventTemplate;
                case "WatchEvent":
                    return WatchEventTemplate;
                default:
                    return DefaultTemplate;
            }
        }
    }
}

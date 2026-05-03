using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using JitHub.Models.Activities;
using JitHub.Models.LegacyGitHub;
using JitHub.Models.PRConversation;
using JitHub.WinUI.ViewModels.Activities;
using JitHub.WinUI.ViewModels.EmojiViewModels;

namespace JitHub.WinUI.Views.Pages.Design;

public sealed partial class DesignLabPage : Page
{
    private readonly Dictionary<string, FrameworkElement> _scenarioMap;

    public Label BugLabel { get; } = new(1, string.Empty, "bug", string.Empty, "F2C94C", "Something is not behaving correctly.", false);
    public Label AccessibilityLabel { get; } = new(2, string.Empty, "accessibility", string.Empty, "7BB99C", "Accessibility and keyboard polish.", false);
    public Label NeedsTriageLabel { get; } = new(3, string.Empty, "needs triage", string.Empty, "D7C39B", "Needs owner review.", false);
    public IReadOnlyList<ActivityCardViewModel> ActivityCards { get; } = ActivityMockData.CreateCards();
    public IReadOnlyList<ConversationNode> PullRequestTimelineItems { get; } = CreatePullRequestTimelineItems();
    public EmojiPanelViewModel DemoEmojiPanelViewModel { get; } = new()
    {
        VotesMap = new Dictionary<ReactionType, Reaction>
        {
            [ReactionType.Plus1] = new()
        },
        UserReactions = new Dictionary<ReactionType, ICollection<string>>
        {
            [ReactionType.Plus1] = ["nerocui"]
        }
    };

    public DesignLabPage()
    {
        InitializeComponent();
        _scenarioMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["buttons"] = ButtonsScenarioCard,
            ["inputs"] = InputsScenarioCard,
            ["segments"] = SegmentsScenarioCard,
            ["segmented"] = SegmentsScenarioCard,
            ["navigation"] = NavigationScenarioCard,
            ["settings"] = SettingsScenarioCard,
            ["repo"] = RepoScenarioCard,
            ["activities"] = ActivitiesScenarioCard,
            ["activity"] = ActivitiesScenarioCard,
            ["conversation"] = ConversationScenarioCard,
            ["pr-timeline"] = PullRequestTimelineScenarioCard,
            ["pull-request-timeline"] = PullRequestTimelineScenarioCard,
            ["timeline"] = PullRequestTimelineScenarioCard,
            ["empty"] = EmptyScenarioCard
        };
        Loaded += DesignLabPage_Loaded;
    }

    public string? RequestedScenario { get; private set; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        RequestedScenario = e.Parameter as string;
    }

    private void DesignLabPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(RequestedScenario))
        {
            return;
        }

        if (_scenarioMap.TryGetValue(RequestedScenario, out FrameworkElement? element))
        {
            foreach (FrameworkElement scenario in _scenarioMap.Values)
            {
                scenario.Visibility = ReferenceEquals(scenario, element)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            _ = DispatcherQueue.TryEnqueue(element.StartBringIntoView);
        }
    }

    private static IReadOnlyList<ConversationNode> CreatePullRequestTimelineItems()
    {
        Repository repo = CreateRepo();
        User actor = CreateUser("cdacamar", "https://avatars.githubusercontent.com/u/1755071?v=4");
        User bot = CreateUser("github-actions[bot]", "https://avatars.githubusercontent.com/in/15368?v=4");
        User reviewer = CreateUser("jtmcdole", "https://avatars.githubusercontent.com/u/1924313?v=4");
        Label bugLabel = new(11, string.Empty, "bug", string.Empty, "F2C94C", "Something is not behaving correctly.", false);
        Milestone milestone = new()
        {
            Number = 7,
            Title = "2026 polish",
            Description = "Pre-publish UI and reliability sweep."
        };

        List<ConversationNode> nodes =
        [
            new CommitNode(
                new PullRequestCommit
                {
                    Author = actor,
                    Committer = actor,
                    Sha = "9648d210e6d7aa23a3c9b9edfb601e0790f2ad1b",
                    Commit = new Commit
                    {
                        Sha = "9648d210e6d7aa23a3c9b9edfb601e0790f2ad1b",
                        Message = "Add Sublime-like fuzzy search to file browser",
                        Author = new Committer
                        {
                            Name = "cdacamar",
                            Email = "cdacamar@example.com",
                            Date = DateTimeOffset.Now.AddHours(-8)
                        }
                    }
                },
                repo,
                42)
        ];

        int index = 0;
        foreach (EventInfoState state in Enum.GetValues<EventInfoState>())
        {
            IssueEvent issueEvent = new()
            {
                Id = 1000 + index,
                NodeId = $"timeline-event-{index}",
                Actor = state is EventInfoState.Labeled or EventInfoState.Unlabeled ? bot : actor,
                Event = state,
                CreatedAt = DateTimeOffset.Now.AddHours(-6).AddMinutes(index * 4),
                CommitId = RequiresCommit(state) ? "4b89ba3eee84c45b6abcc9216af35bf57ec0b324" : string.Empty,
                Label = state is EventInfoState.Labeled or EventInfoState.Unlabeled ? bugLabel : null,
                Assignee = state is EventInfoState.Assigned or EventInfoState.Unassigned ? reviewer : null,
                Assigner = state is EventInfoState.Assigned ? actor : null,
                RequestedReviewer = state is EventInfoState.ReviewRequested or EventInfoState.ReviewRequestRemoved ? reviewer : null,
                RequestedTeam = state is EventInfoState.ReviewRequested ? new Team { Name = "windows-ui", Slug = "windows-ui" } : null,
                ReviewRequester = state is EventInfoState.ReviewRequested ? actor : null,
                Milestone = state is EventInfoState.Milestoned or EventInfoState.Demilestoned ? milestone : null,
                LockReason = state is EventInfoState.Locked ? "resolved" : string.Empty,
                Rename = state is EventInfoState.Renamed
                    ? new RenameInfo("Search box does not keep focus", "Polish search focus and suggestions")
                    : null
            };

            nodes.Add(new EventNode(issueEvent, repo, 42));
            index++;
        }

        return nodes;
    }

    private static bool RequiresCommit(EventInfoState state)
    {
        return state is EventInfoState.Merged
            or EventInfoState.HeadRefForcePushed
            or EventInfoState.Referenced
            or EventInfoState.CommitCommented
            or EventInfoState.Committed;
    }

    private static Repository CreateRepo()
    {
        return new Repository
        {
            Id = 775923501,
            Name = "JitHubV2",
            FullName = "JitHubApp/JitHubV2",
            DefaultBranch = "main",
            Owner = CreateUser("JitHubApp", "https://avatars.githubusercontent.com/u/168522333?v=4")
        };
    }

    private static User CreateUser(string login, string avatarUrl)
    {
        return new User
        {
            Login = login,
            Name = login,
            AvatarUrl = avatarUrl
        };
    }
}

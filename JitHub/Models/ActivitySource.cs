using JitHub.Services;
using JitHub.ViewModels.ActivityViewModels;
using CommunityToolkit.Mvvm.DependencyInjection;
using Octokit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Common.Collections;

namespace JitHub.Models
{
    public class ActivitySource : IIncrementalSource<ActivityViewModel>
    {
        private IGitHubService _gitHubService;
        private ICommand _loadCommand;
        private ICommand _finishCommand;
        public ActivitySource(ICommand loadCommand, ICommand finishCommand)
        {
            _gitHubService = Ioc.Default.GetService<IGitHubService>();
            _loadCommand = loadCommand;
            _finishCommand = finishCommand;
        }
        public async Task<IEnumerable<ActivityViewModel>> GetPagedItemsAsync(int pageIndex, int pageSize, CancellationToken cancellationToken = default)
        {
            _loadCommand.Execute(null);
            var user = await _gitHubService.GitHubClient.User.Current();
            var options = new ApiOptions
            {
                StartPage = pageIndex + 1,
                PageCount = 1,
                PageSize = pageSize
            };
            ICollection<ActivityViewModel> res;
            
            try
            {
                var activities = await _gitHubService.GetActivities(user.Login, options);
                foreach (var activity in activities)
                {
                    if (string.IsNullOrEmpty(activity.Actor.AvatarUrl))
                    {
                        throw new Exception("Invalid avatar");
                    }
                }
                res = activities
                    .Select(activity =>
                    {
                        switch (activity.Type)
                        {
                            case "CommitCommentEvent":
                                return new CommitCommentActivityViewModel(activity);
                            case "CreateEvent":
                                return new CreateActivityViewModel(activity);
                            case "DeleteEvent":
                                return new DeleteActivityViewModel(activity);
                            case "ForkEvent":
                                return new ForkActivityViewModel(activity);
                            case "GollumEvent":
                                return new GollumActivityViewModel(activity);
                            case "IssueCommentEvent":
                                return new IssueCommentActivityViewModel(activity);
                            case "IssuesEvent":
                                return new IssueActivityViewModel(activity);
                            case "MemberEvent":
                                return new MemberActivityViewModel(activity);
                            case "PublicEvent":
                                return new PublicActivityViewModel(activity);
                            case "PullRequestEvent":
                                return new PullRequestActivityViewModel(activity);
                            case "PullRequestReviewCommentEvent":
                                return new PullRequestReviewActivityViewModel(activity);
                            case "PushEvent":
                                return new PushActivityViewModel(activity);
                            case "ReleaseEvent":
                                return new ReleaseActivityViewModel(activity);
                            case "SponsorshipEvent":
                                return new SponsorshipActivityViewModel(activity);
                            case "WatchEvent":
                                return new WatchActivityViewModel(activity);
                            default:
                                return new ActivityViewModel(activity);
                        }
                    })
                    .ToList();
            }
            catch
            {
                res = new List<ActivityViewModel>();
            }
            _finishCommand.Execute(null);
            return res;
        }
    }
}

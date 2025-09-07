using CommunityToolkit.Labs.WinUI.MarkdownTextBlock;
using JitHub.ViewModels.Base;
using Octokit;
using System;

namespace JitHub.ViewModels.ActivityViewModels
{
    public class ActivityViewModel : RepoViewModel
    {
        private User _sender;
        private bool _public;
        private User _actor;
        private Organization _org;
        private DateTimeOffset _createdAt;
        private string _id;
        private MarkdownConfig _markdownConfig;
        private string _markdownText;

        public MarkdownConfig MarkdownConfig
        {
            get => _markdownConfig;
            set => SetProperty(ref _markdownConfig, value);
        }
        public string MarkdownText
        {
            get => _markdownText;
            set => SetProperty(ref _markdownText, value);
        }
        public User Sender
        {
            get => _sender;
            set => SetProperty(ref _sender, value);
        }
        public bool Public
        {
            get => _public;
            set => SetProperty(ref _public, value);
        }
        public User Actor
        {
            get => _actor;
            set => SetProperty(ref _actor, value);
        }
        public Organization Org
        {
            get => _org;
            set => SetProperty(ref _org, value);
        }
        public DateTimeOffset CreatedAt
        {
            get => _createdAt;
            set => SetProperty(ref _createdAt, value);
        }
        public string Id
        {
            get => _id;
            set => SetProperty(ref _id, value);
        }
        public string RepoName => Repo?.FullName == null ? Repo?.Name : Repo?.FullName;
        public string Type { get; set; }
        public ActivityViewModel(Activity activity)
        {
            Sender = activity.Payload.Sender;
            Repo = activity.Repo;
            Public = activity.Public;
            Actor = activity.Actor;
            Org = activity.Org;
            CreatedAt = activity.CreatedAt;
            Id = activity.Id;
            Type = activity.Type;
        }
    }
}

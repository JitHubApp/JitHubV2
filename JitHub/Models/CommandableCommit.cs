using JitHub.Models.Base;
using Octokit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

namespace JitHub.Models
{
    public class CommandableCommit : RepoSelectableItemModel<Commit>
    {
        private ICommand _copy;
        private ICommand _viewCode;
        private string _sha;
        private string _message;
        private string _avatarUrl;
        private string _login;
        private DateTimeOffset _date;
        private Commit _commit;
        public ICommand Copy
        {
            get => _copy;
            set => SetProperty(ref _copy, value);
        }
        public ICommand ViewCode
        {
            get => _viewCode;
            set => SetProperty(ref _viewCode, value);
        }
        public string Sha
        {
            get => _sha;
            set => SetProperty(ref _sha, value);
        }
        public string Message
        {
            get => _message;
            set => SetProperty(ref _message, value);
        }
        public string AvatarUrl
        {
            get => _avatarUrl;
            set => SetProperty(ref _avatarUrl, value);
        }
        public string Login
        {
            get => _login;
            set => SetProperty(ref _login, value);
        }
        public DateTimeOffset Date
        {
            get => _date;
            set => SetProperty(ref _date, value);
        }
        public Commit Commit
        {
            get => _commit;
            set => SetProperty(ref _commit, value);
        }

        public CommandableCommit(Repository repo, ICommand copy, ICommand viewCode, PullRequestCommit commit)
        {
            Repository = repo;
            Copy = copy;
            ViewCode = viewCode;
            Model = commit.Commit;
            Sha = commit.Sha;
            Message = commit.Commit.Message;
            AvatarUrl = commit.Author.AvatarUrl;
            Login = commit.Author.Login;
            Date = commit.Commit.Author.Date;
            Commit = commit.Commit;
        }

        public CommandableCommit(Repository repo, ICommand copy, ICommand viewCode, GitHubCommit commit)
        {
            Repository = repo;
            Copy = copy;
            ViewCode = viewCode;
            Model = commit.Commit;
            Sha = commit.Sha;
            Message = commit.Commit.Message;
            AvatarUrl = commit.Author?.AvatarUrl;
            Login = commit.Author?.Login;
            Date = commit.Commit.Author.Date;
            Commit = commit.Commit;
        }
    }
}

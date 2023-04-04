using JitHub.Models;
using JitHub.Models.CommandArgs;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Input;

namespace JitHub.Services
{
    public class CommandService : ICommandService
    {
        private Dictionary<JitHubCommand, ICommand> _commands = new Dictionary<JitHubCommand, ICommand>();
        private IGitHubService _gitHubService;
        private ModalService _modalService;

        public CommandService(IGitHubService gitHubService, ModalService modalService)
        {
            _gitHubService = gitHubService;
            _modalService = modalService;
            RegisterCommand(JitHubCommand.CreateNewIssue, new AsyncRelayCommand<IssueFormArgs>(CreateNewIssue));
            RegisterCommand(JitHubCommand.UpdateIssue, new AsyncRelayCommand<IssueFormArgs>(UpdateIssue));
        }

        public ICommand GetCommand(JitHubCommand commandName)
        {
            if (!_commands.ContainsKey(commandName)) throw new NotImplementedException();
            return _commands[commandName];
        }

        public void RegisterCommand(JitHubCommand commandName, ICommand command)
        {
            _commands.Add(commandName, command);
        }

        private async Task CreateNewIssue(IssueFormArgs args)
        {
            if (string.IsNullOrWhiteSpace(args.Title))
                return;
            await _gitHubService.PostNewIssue(args.RepoId, args.Title, args.Body);
            _modalService.Close();
        }

        private async Task UpdateIssue(IssueFormArgs args)
        {
            if (string.IsNullOrWhiteSpace(args.Title))
                return;
            await _gitHubService.Updateissue(args.RepoId, args.Issue.Number, new Octokit.IssueUpdate() { Title = args.Title, Body = args.Body });
            _modalService.Close();
        }
    }
}

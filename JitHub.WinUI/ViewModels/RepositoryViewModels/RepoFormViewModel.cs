using JitHub.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using JitHub.Models.LegacyGitHub;
using NewRepository = JitHub.Models.LegacyGitHub.NewRepository;
using RepositoryVisibility = JitHub.Models.LegacyGitHub.RepositoryVisibility;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.WinUI.ViewModels.RepositoryViewModels
{
    public class RepoFormViewModel : ObservableObject
    {
        private readonly IGitHubService _gitHubService;
        private string _name = string.Empty;
        private string _error = string.Empty;
        private string _description = string.Empty;
        private List<RepositoryVisibility> _visibilities = [];
        private RepositoryVisibility _selectedVisibility;
        private bool _createReadme;
        private List<Models.License> _licenses = [];
        private Models.License? _selectedLicense;
        private ICommand? _refreshCommand;
        private ModalSession? _modalSession;

        public string Name
        {
            get => _name;
            set
            {
                if (SetProperty(ref _name, value))
                {
                    CreateCommand.NotifyCanExecuteChanged();
                }
            }
        }
        public string Error
        {
            get => _error;
            set => SetProperty(ref _error, value);
        }
        public string Description
        {
            get => _description;
            set => SetProperty(ref _description, value);
        }
        public List<RepositoryVisibility> Visibilities
        {
            get => _visibilities;
            set => SetProperty(ref _visibilities, value);
        }
        public RepositoryVisibility SelectedVisibility
        {
            get => _selectedVisibility;
            set => SetProperty(ref _selectedVisibility, value);
        }
        public bool CreateReadme
        {
            get => _createReadme;
            set => SetProperty(ref _createReadme, value);
        }
        public List<Models.License> Licenses
        {
            get => _licenses;
            set => SetProperty(ref _licenses, value);
        }
        public Models.License? SelectedLicense
        {
            get => _selectedLicense;
            set
            {
                if (SetProperty(ref _selectedLicense, value))
                {
                    OnPropertyChanged(nameof(LicenseConsequenceText));
                }
            }
        }
        public string LicenseConsequenceText =>
            SelectedLicense?.ConsequenceText ?? "Choose a license intentionally to add one to the new repository.";
        public IAsyncRelayCommand CreateCommand { get; }

        public RepoFormViewModel()
        {
            Licenses = Models.License.GetLicenses().ToList();
            Visibilities = new List<RepositoryVisibility>()
            {
                RepositoryVisibility.Internal,
                RepositoryVisibility.Private,
                RepositoryVisibility.Public,
            };
            SelectedVisibility = RepositoryVisibility.Public;
            SelectedLicense = Licenses.Single(license => license.IsNoLicense);
            _gitHubService = Ioc.Default.GetService<IGitHubService>()
                ?? throw new InvalidOperationException("IGitHubService is not registered.");
            CreateCommand = new AsyncRelayCommand(
                CreateNewRepo,
                () => !string.IsNullOrWhiteSpace(Name));
        }

        public void Init(ICommand refreshCommand)
        {
            _refreshCommand = refreshCommand;
        }

        public void AttachModalSession(ModalSession session) => _modalSession = session;

        public void OnNameChange(object sender, TextChangedEventArgs e)
        {
            Error = string.Empty;
        }

        public async Task CreateNewRepo()
        {
            if (!string.IsNullOrWhiteSpace(Name) &&
                _modalSession is { } session &&
                session.TryBeginMutation())
            {
                bool created = false;
                try
                {
                    Error = string.Empty;
                    var repo = new NewRepository(Name.Trim());
                    if (!string.IsNullOrWhiteSpace(Description))
                    {
                        repo.Description = Description.Trim();
                    }
                    repo.Visibility = SelectedVisibility;
                    repo.Private = repo.Visibility == RepositoryVisibility.Private || repo.Visibility == RepositoryVisibility.Internal;
                    repo.AutoInit = CreateReadme;
                    repo.LicenseTemplate = SelectedLicense?.TemplateName;
                    _ = await _gitHubService.CreateNewRepo(repo);
                    created = true;
                }
                catch (Exception e)
                {
                    Error = e.Message;
                }
                finally
                {
                    session.EndMutation();
                }

                if (!created)
                {
                    return;
                }

                _ = session.TryClose();
                try
                {
                    if (_refreshCommand is not null && _refreshCommand.CanExecute(null))
                    {
                        _refreshCommand.Execute(null);
                    }
                }
                catch (Exception refreshException)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"Repository created, but the repository rail refresh failed: {refreshException}");
                }
            }
        }
    }
}





using JitHub.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using Octokit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using Windows.UI.Xaml.Controls;

namespace JitHub.ViewModels.RepositoryViewModels
{
    public class RepoFormViewModel : ObservableObject
    {
        private IGitHubService _gitHubService;
        private ModalService _modalService;
        private string _name;
        private string _error;
        private string _description;
        private ICollection<RepositoryVisibility> _visibilities;
        private RepositoryVisibility _selectedVisibility;
        private bool _createReadme;
        private ICollection<Models.License> _licenses;
        private Models.License _selectedLicense;
        private ICommand _refreshCommand;

        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
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
        public ICollection<RepositoryVisibility> Visibilities
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
        public ICollection<Models.License> Licenses
        {
            get => _licenses;
            set => SetProperty(ref _licenses, value);
        }
        public Models.License SelectedLicense
        {
            get => _selectedLicense;
            set => SetProperty(ref _selectedLicense, value);
        }

        public RepoFormViewModel()
        {
            Licenses = Models.License.GetLicenses();
            Visibilities = new List<RepositoryVisibility>()
            {
                RepositoryVisibility.Internal,
                RepositoryVisibility.Private,
                RepositoryVisibility.Public,
            };
            SelectedVisibility = Visibilities.FirstOrDefault((visibility) => visibility == RepositoryVisibility.Public);
            _gitHubService = Ioc.Default.GetService<IGitHubService>();
            _modalService = Ioc.Default.GetService<ModalService>();
        }

        public void Init(ICommand refreshCommand)
        {
            _refreshCommand = refreshCommand;
        }

        public void OnNameChange(object sender, TextChangedEventArgs e)
        {
            Error = string.Empty;
        }

        public async Task CreateNewRepo()
        {
            if (!string.IsNullOrWhiteSpace(Name))
            {
                var repo = new NewRepository(Name);
                if (!string.IsNullOrWhiteSpace(Description))
                {
                    repo.Description = Description;
                }
                repo.Visibility = SelectedVisibility;
                repo.Private = repo.Visibility == RepositoryVisibility.Private || repo.Visibility == RepositoryVisibility.Internal;
                repo.AutoInit = CreateReadme;
                if (SelectedLicense != null)
                {
                    repo.LicenseTemplate = SelectedLicense.Name;
                }
                try
                {
                    var newRepo = await _gitHubService.CreateNewRepo(repo);
                    if (_refreshCommand != null && _refreshCommand.CanExecute(null))
                    {
                        _refreshCommand.Execute(null);
                    }
                    _modalService.Close();
                }
                catch (Exception e)
                {
                    Error = e.Message;
                }
            }
        }
    }
}

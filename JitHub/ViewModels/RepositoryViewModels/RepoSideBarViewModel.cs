using JitHub.Models;
using JitHub.Services;
using JitHub.ViewModels.Base;
using JitHub.Views.Controls.Common;
using JitHub.Views.Controls.Repo;
using JitHub.Views.Pages;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using JitHub.Models.Base;
using Microsoft.UI.Xaml.Controls;
using CommunityToolkit.Labs.WinUI;

namespace JitHub.ViewModels.RepositoryViewModels;

public partial class RepoSideBarViewModel : RepoListViewModel<RepoModel>
{
    private ModalService _modalService;
    private NavigationService _navigationService;
    [ObservableProperty]
    private int _selectedIndex;
    [ObservableProperty]
    private RepoType _repositoryPublicity;

    public FeatureService FeatureService;
    public RepoSideBarViewModel()
    {
        _modalService = Ioc.Default.GetService<ModalService>();
        _navigationService = Ioc.Default.GetService<NavigationService>();
        FeatureService = Ioc.Default.GetService<FeatureService>();
    }

    public override async Task<ICollection<RepoModel>> GetRepos()
    {
        var repos = await GitHubService.GetAllRepos();
        var repoModels = repos
            .Select(repo => new RepoModel(repo))
            .ToList();
        return repoModels;
    }

    public void OnNewRepo()
    {
        _modalService.Open("New Repository", new RepoForm(new AsyncRelayCommand(LoadRepos)));
    }

    private void CloseModal()
    {
        _modalService?.Close();
    }

    public async Task BuyProFeature()
    {
        var res = await FeatureService.BuyProFeature();
        _modalService.Close();
        switch (res)
        {
            // TODO: Add reactions to all these cases
            case FeaturePurchaseState.Success:
                _modalService.Open("Thank you!", new ProLicensePurchaseSuccessDialog(new RelayCommand(CloseModal)));
                break;
            case FeaturePurchaseState.Failure:
                break;
            case FeaturePurchaseState.AlreadyOwn:
                break;
            default:
                break;
        }
    }

    public void OnManageRepo()
    {
        if (FeatureService.ProLicense)
        {
            _navigationService.NavigateTo("Manage Repository", typeof(RepoManagePage));
        }
        else
        {
            var buyCommand = new AsyncRelayCommand(BuyProFeature);
            var cancelCommand = new RelayCommand(CloseModal);
            _modalService.Open(new FeaturePurchaseDialog(buyCommand, cancelCommand));
        }
    }

    public void SegmentedSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var segmentedControl = sender as Segmented;
        var selectedIndex = segmentedControl.SelectedIndex;
        RepositoryPublicity = (selectedIndex) switch
        {
            0 => RepoType.Public,
            1 => RepoType.Private,
            2 => RepoType.Forked,
            _ => RepoType.Public,
        };
    }

    public override bool IsForked(RepoModel repo)
    {
        return repo.Repository.Fork;
    }

    public override bool IsPrivate(RepoModel repo)
    {
        return repo.Repository.Private;
    }

    public override bool IsPublic(RepoModel repo)
    {
        return !repo.Repository.Private;
    }

    public override ICollection<RepoModel> NewRepoList()
    {
        return new List<RepoModel>();
    }
}

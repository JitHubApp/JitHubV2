using JitHub.Models;
using JitHub.Services;
using JitHub.WinUI.ViewModels.Base;
using JitHub.WinUI.Views.Controls.Common;
using JitHub.WinUI.Views.Controls.Repo;
using JitHub.WinUI.Views.Pages;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using JitHub.Models.Base;
using Microsoft.UI.Xaml.Controls;
using CommunityToolkit.WinUI.Controls;

namespace JitHub.WinUI.ViewModels.RepositoryViewModels;

public partial class RepoSideBarViewModel : RepoListViewModel<RepoModel>
{
    private readonly ModalService _modalService;
    private readonly NavigationService _navigationService;
    [ObservableProperty]
    public partial int SelectedIndex { get; set; }

    [ObservableProperty]
    public partial RepoType RepositoryPublicity { get; set; }

    public FeatureService FeatureService { get; }
    public RepoSideBarViewModel()
    {
        _modalService = Ioc.Default.GetService<ModalService>()
            ?? throw new InvalidOperationException("ModalService is not registered.");
        _navigationService = Ioc.Default.GetService<NavigationService>()
            ?? throw new InvalidOperationException("NavigationService is not registered.");
        FeatureService = Ioc.Default.GetService<FeatureService>()
            ?? throw new InvalidOperationException("FeatureService is not registered.");
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
        _modalService.Close();
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
        if (sender is not Segmented segmentedControl)
        {
            return;
        }

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




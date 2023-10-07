﻿using Octokit;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using Page = Windows.UI.Xaml.Controls.Page;

// The Blank Page item template is documented at https://go.microsoft.com/fwlink/?LinkId=234238

namespace JitHub.Views.Pages
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class RepoDetailPage : Page
    {
        public RepoDetailPage()
        {
            this.InitializeComponent();
            ViewModel.Frame = RepoDetailFrame;
        }

        override protected void OnNavigatedTo(NavigationEventArgs e)
        {
            ViewModel.OnNavigatedTo(e);
        }
    }
}

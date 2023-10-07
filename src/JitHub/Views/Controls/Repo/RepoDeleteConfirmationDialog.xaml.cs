using JitHub.Services;
using CommunityToolkit.Mvvm.DependencyInjection;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Input;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace JitHub.Views.Controls.Repo
{
    public sealed partial class RepoDeleteConfirmationDialog : UserControl
    {
        private ISettingService _settings;
        public int Number { get; }
        public ICommand ConfirmCommand { get; }
        public ICommand CancelCommand { get; }
        public RepoDeleteConfirmationDialog(int number, ICommand confirmCommand, ICommand cancelCommand)
        {
            this.InitializeComponent();
            Number = number;
            ConfirmCommand = confirmCommand;
            CancelCommand = cancelCommand;
            _settings = Ioc.Default.GetService<ISettingService>();
        }

        private void CheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox c)
            {
                _settings.Save(AccountService.doNotWarnDeleteRepoKey, c.IsChecked);
            }
        }
    }
}

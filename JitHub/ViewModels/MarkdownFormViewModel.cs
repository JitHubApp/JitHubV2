using CommunityToolkit.Labs.WinUI.MarkdownTextBlock;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using JitHub.Services;
using Microsoft.UI.Xaml.Controls;
using System.Windows.Input;

namespace JitHub.ViewModels
{
    public class MarkdownFormViewModel : ObservableObject
    {
        private string _text;
        private string _selectedBodyView = "Write";
        private ICommand _submitCommand;
        private MarkdownConfig _markdownConfig;
        private IGitHubService _gitHubService;

        public MarkdownConfig MarkdownConfig
        {
            get => _markdownConfig;
            set => SetProperty(ref _markdownConfig, value);
        }
        
        public string Text
        {
            get => _text;
            set
            {
                SetProperty(ref _text, value);
                SetProperty(ref _markdownConfig, _gitHubService.GetMarkdownConfig());
            }
        }

        public string SelectedBodyView
        {
            get => _selectedBodyView;
            set => SetProperty(ref _selectedBodyView, value);
        }
        public ICommand SubmitCommand
        { 
            get => _submitCommand;
            set => SetProperty(ref _submitCommand, value);
        }

        public void OnNavChange(NavigationView sender, NavigationViewItemInvokedEventArgs args)
        {
            SelectedBodyView = (string)args.InvokedItem;
        }

        public void OnSubmit()
        {
            if (SubmitCommand != null)
            {
                SubmitCommand.Execute(Text);
            }
        }
        
        public MarkdownFormViewModel()
        {
            _gitHubService = Ioc.Default.GetService<IGitHubService>();
        }
    }
}

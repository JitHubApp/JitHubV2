using CommunityToolkit.WinUI.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using JitHub.Services;
using System;
using System.Windows.Input;

namespace JitHub.WinUI.ViewModels
{
    public class MarkdownFormViewModel : ObservableObject
    {
        private string _text = string.Empty;
        private string _selectedBodyView = "Write";
        private ICommand? _submitCommand;
        private MarkdownConfig _markdownConfig = null!;
        private readonly IGitHubService _gitHubService;

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
        public ICommand? SubmitCommand
        { 
            get => _submitCommand;
            set => SetProperty(ref _submitCommand, value);
        }

        public void OnSubmit()
        {
            if (SubmitCommand is not null)
            {
                SubmitCommand.Execute(Text);
            }
        }
        
        public MarkdownFormViewModel()
        {
            _gitHubService = Ioc.Default.GetService<IGitHubService>()
                ?? throw new InvalidOperationException("IGitHubService is not registered.");
            MarkdownConfig = _gitHubService.GetMarkdownConfig();
        }
    }
}




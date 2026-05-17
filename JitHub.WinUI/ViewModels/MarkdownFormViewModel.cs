using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Input;

namespace JitHub.WinUI.ViewModels
{
    public class MarkdownFormViewModel : ObservableObject
    {
        private string _text = string.Empty;
        private string _selectedBodyView = "Write";
        private ICommand? _submitCommand;
        
        public string Text
        {
            get => _text;
            set => SetProperty(ref _text, value);
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
        
        public MarkdownFormViewModel() { }
    }
}




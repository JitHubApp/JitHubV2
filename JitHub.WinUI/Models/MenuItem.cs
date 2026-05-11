using System.Windows.Input;

namespace JitHub.Models
{
    [WinRT.GeneratedBindableCustomProperty]
    public partial class MenuItem
    {
        public string Text { get; set; } = string.Empty;
        public ICommand Command { get; set; }
        public object? Parameter { get; set; }

        public MenuItem(string text, ICommand command, object? param = null)
        {
            Text = text;
            Command = command;
            Parameter = param;
        }
    }
}

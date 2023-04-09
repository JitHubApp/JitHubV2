using System.Windows.Input;

namespace JitHub.Models
{
    public class MenuItem
    {
        public string Text { get; set; }
        public ICommand Command { get; set; }
        public object Parameter { get; set; }

        public MenuItem(string text, ICommand command, object param = null)
        {
            Text = text;
            Command = command;
            Parameter = param;
        }
    }
}

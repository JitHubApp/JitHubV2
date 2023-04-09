using JitHub.Models.Base;
using Octokit;
using System.Windows.Input;

namespace JitHub.Models
{
    public class SelectableLabel : SelectableItem
    {
        private Label _label;

        public Label Label
        {
            get => _label;
            set => SetProperty(ref _label, value);
        }

        public SelectableLabel(Label label, ICommand command)
        {
            Label = label;
            if (command != null)
            {
                SelectionCommand = command;
            }
            else
            {
                Selectable = false;
            }
            Type = "Label";
        }
    }
}

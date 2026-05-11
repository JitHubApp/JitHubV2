using JitHub.Models.Base;
using JitHub.Models.LegacyGitHub;
using System.Windows.Input;

namespace JitHub.Models
{
    [WinRT.GeneratedBindableCustomProperty]
    public partial class SelectableLabel : SelectableItem
    {
        private Label _label = null!;

        public Label Label
        {
            get => _label;
            set => SetProperty(ref _label, value);
        }

        public SelectableLabel(Label label, ICommand? command)
        {
            Label = label;
            if (command is not null)
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

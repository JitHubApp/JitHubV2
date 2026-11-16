using Microsoft.UI.Xaml;

namespace JitHub.WinUI.Models.Navigation;

public class ModalArg
{
    public string Title { get; set; } = string.Empty;

    public bool UseHeader { get; set; }

    public FrameworkElement? Content { get; set; }
}

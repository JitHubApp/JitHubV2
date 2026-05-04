using Microsoft.UI.Xaml;

namespace JitHub.Models.NavArgs;

public class ModalArg
{
    public string Title { get; set; } = string.Empty;

    public bool UseHeader { get; set; }

    public FrameworkElement? Content { get; set; }
}

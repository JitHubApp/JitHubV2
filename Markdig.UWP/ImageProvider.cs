using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;

namespace Markdig.UWP;

public interface IImageProvider
{
    Task<Image> GetImage(string url);
    bool ShouldUseThisProvider(string url);
}

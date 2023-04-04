using System.Threading.Tasks;
using Windows.UI.Xaml.Controls;

namespace Markdig.UWP;

public interface ISVGRenderer
{
    Task<Image> SvgToImage(string svgString);
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.UI.Xaml;

namespace JitHub.Services
{
    public interface IMarkdownService
    {
        string ParseGFM(string gfm, ApplicationTheme theme);
    }
}

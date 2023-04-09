using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;

namespace JitHub.Services
{
    public interface IThemeService
    {
        void SetTheme(string theme);
        ApplicationTheme GetSystemTheme();
        ApplicationTheme GetApplicationTheme();
        string GetTheme();
    }
}

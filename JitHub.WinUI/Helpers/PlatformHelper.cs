using System;
using System.Diagnostics;
using Windows.ApplicationModel.DataTransfer;

namespace JitHub.WinUI.Helpers
{
    public static class PlatformHelper
    {
        public static bool CopyString(string? content)
        {
            if (string.IsNullOrEmpty(content))
            {
                return false;
            }

            try
            {
                var dataPackage = new DataPackage();
                dataPackage.SetText(content);
                Clipboard.SetContent(dataPackage);
                Clipboard.Flush();
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Clipboard write failed: {ex.GetType().Name}");
                return false;
            }
        }
    }
}


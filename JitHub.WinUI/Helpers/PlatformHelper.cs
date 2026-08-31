using System;
using System.Threading;
using Windows.ApplicationModel.DataTransfer;

namespace JitHub.WinUI.Helpers
{
    public static class PlatformHelper
    {
        private static int _clipboardFailureReported;

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
                if (Interlocked.Exchange(ref _clipboardFailureReported, 1) == 0)
                {
                    HandledFailureReporter.Report(ex, "ui-clipboard-write");
                }
                return false;
            }
        }
    }
}


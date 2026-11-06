using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace MarkdownRenderer.Sample;

public static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Microsoft.UI.Xaml.Application.Start((Microsoft.UI.Xaml.ApplicationInitializationCallbackParams p) =>
        {
            var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
    }
}

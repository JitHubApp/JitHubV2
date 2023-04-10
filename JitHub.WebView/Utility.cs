// Some code are from https://github.com/microsoft/microsoft-ui-xaml/
#nullable enable
using System;
using Windows.System.Threading;
using Windows.UI.Core;

namespace WebView2Ex;

static class Utility
{
    public static void ScheduleActionAfterWait(CoreDispatcher Dispatcher,
            Action action,
            uint millisecondWait)
    {
        // The callback that is given to CreateTimer is called off of the UI thread.
        // In order to make this useful by making it so we can interact with XAML objects,
        // we'll use the dispatcher to first post our work to the UI thread before executing it.
        var timer = ThreadPoolTimer.CreateTimer(async _
            => await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => action()),
            TimeSpan.FromMilliseconds(millisecondWait)
        );
    }
}

using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace JitHub.WinUI;

internal static class Program
{
    private const string AppInstanceKey = "JitHub";

    private static readonly object ActivationGate = new();
    private static readonly Queue<AppActivationArguments> PendingActivations = new();
    private static App? _app;

    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        AppActivationArguments activationArguments = AppInstance.GetCurrent().GetActivatedEventArgs();
        AppInstance keyInstance = AppInstance.FindOrRegisterForKey(AppInstanceKey);

        if (!keyInstance.IsCurrent)
        {
            keyInstance.RedirectActivationToAsync(activationArguments).AsTask().GetAwaiter().GetResult();
            return 0;
        }

        keyInstance.Activated += OnActivated;

        Application.Start(_ =>
        {
            var synchronizationContext = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(synchronizationContext);

            _app = new App();
            _app.HandleActivation(activationArguments);
            DrainPendingActivations();
        });

        return 0;
    }

    private static void OnActivated(object? sender, AppActivationArguments activationArguments)
    {
        App? app = _app;
        if (app is not null)
        {
            app.HandleActivation(activationArguments);
            return;
        }

        lock (ActivationGate)
        {
            app = _app;
            if (app is not null)
            {
                app.HandleActivation(activationArguments);
                return;
            }

            PendingActivations.Enqueue(activationArguments);
        }
    }

    private static void DrainPendingActivations()
    {
        App? app = _app;
        if (app is null)
        {
            return;
        }

        while (true)
        {
            AppActivationArguments? activationArguments = null;

            lock (ActivationGate)
            {
                if (PendingActivations.Count > 0)
                {
                    activationArguments = PendingActivations.Dequeue();
                }
            }

            if (activationArguments is null)
            {
                break;
            }

            app.HandleActivation(activationArguments);
        }
    }
}

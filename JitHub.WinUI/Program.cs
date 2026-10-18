using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace JitHub.WinUI;

internal static class Program
{
    private const string AppInstanceKey = "JitHub";

    private static readonly object ActivationGate = new();
    private static readonly Queue<AppActivationArguments> PendingActivations = new();
    private static App? _app;

    internal static LaunchOptions CurrentLaunchOptions { get; private set; } = new();

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            CurrentLaunchOptions = LaunchOptions.Parse(args);

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
                try
                {
                    var synchronizationContext = new DispatcherQueueSynchronizationContext(
                        DispatcherQueue.GetForCurrentThread());
                    SynchronizationContext.SetSynchronizationContext(synchronizationContext);

                    _app = new App();
                    _app.HandleActivation(activationArguments);
                    DrainPendingActivations();
                }
                catch (Exception ex)
                {
                    LogStartupException(ex);
                    throw;
                }
            });

            return 0;
        }
        catch (Exception ex)
        {
            LogStartupException(ex);
            return Marshal.GetHRForException(ex);
        }
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

    private static void LogStartupException(Exception ex)
    {
        try
        {
            string logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "JitHub",
                "logs");
            Directory.CreateDirectory(logDirectory);
            string logPath = Path.Combine(logDirectory, "startup-error.log");
            string entry =
                $"[{DateTimeOffset.Now:O}]{Environment.NewLine}{ex}{Environment.NewLine}{new string('-', 80)}{Environment.NewLine}";
            File.AppendAllText(logPath, entry);
        }
        catch
        {
        }
    }
}

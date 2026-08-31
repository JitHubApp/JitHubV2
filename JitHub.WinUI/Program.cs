using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.Globalization;
using Microsoft.Windows.AppLifecycle;
using JitHub.Services.Markdown;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Windows.ApplicationModel.Activation;

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
            LogStartupPhase("main.enter");
            WinRT.ComWrappersSupport.InitializeComWrappers();
            LogStartupPhase("main.com-wrappers-ready");
            AppActivationArguments activationArguments = AppInstance.GetCurrent().GetActivatedEventArgs();
            CurrentLaunchOptions = LaunchOptions.Parse(args, GetLaunchArgumentText(activationArguments));
            JitHub.Services.RepositoryActionAutomationScenario.ConfigureWebsiteShowcase(
                CurrentLaunchOptions.WebsiteShowcase);
            MarkdownLifecycleAutomationBridge.ConfigureLaunchOptions(
                CurrentLaunchOptions.MarkdownLifecycleFixture,
                CurrentLaunchOptions.MarkdownLifecycleHost);
            ConfigureAutomationLanguageOverride();
            LogStartupPhase("main.launch-options-ready");

            string appInstanceKey = CurrentLaunchOptions.HasPageOverride
                ? $"{AppInstanceKey}-{Environment.ProcessId}"
                : AppInstanceKey;
            AppInstance keyInstance = AppInstance.FindOrRegisterForKey(appInstanceKey);
            LogStartupPhase($"main.app-instance-ready:{keyInstance.IsCurrent}");

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
                    LogStartupPhase("app-start.callback-enter");
                    var synchronizationContext = new DispatcherQueueSynchronizationContext(
                        DispatcherQueue.GetForCurrentThread());
                    SynchronizationContext.SetSynchronizationContext(synchronizationContext);
                    LogStartupPhase("app-start.dispatcher-ready");

                    // WinUI establishes its XAML resource context during Application.Start.
                    // Reapply the automation qualifier on that dispatcher before App.xaml
                    // or any page XAML is constructed.
                    ConfigureAutomationLanguageOverride();
                    _app = new App();
                    LogStartupPhase("app-start.app-constructed");
                    _app.HandleActivation(activationArguments);
                    LogStartupPhase("app-start.activation-requested");
                    DrainPendingActivations();
                    LogStartupPhase("app-start.callback-exit");
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

    private static string? GetLaunchArgumentText(AppActivationArguments activationArguments) =>
        activationArguments.Kind == ExtendedActivationKind.Launch &&
        activationArguments.Data is ILaunchActivatedEventArgs launchArguments
            ? launchArguments.Arguments
            : null;

    private static void ConfigureAutomationLanguageOverride()
    {
        const string pseudoLanguage = "qps-ploc";
#if PSEUDO_LOCALIZATION_BUILD
        const bool isPseudoLocalizationBuild = true;
#else
        const bool isPseudoLocalizationBuild = false;
#endif
        bool canUsePseudoLocalization = isPseudoLocalizationBuild || !string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable("JITHUB_AUTOMATION_DATA_ROOT"));
        bool usePseudoLocalization = string.Equals(
            CurrentLaunchOptions.Scenario,
            "vnext-pseudo-localized",
            StringComparison.OrdinalIgnoreCase);

        if (usePseudoLocalization && canUsePseudoLocalization)
        {
            // Set the override before App.xaml or any page XAML resolves x:Uid resources.
            ApplicationLanguages.PrimaryLanguageOverride = pseudoLanguage;
            return;
        }

        // Normal product builds do not package qps-ploc, so a persisted automation
        // override cannot resolve pseudo resources on a later product launch.
    }

    internal static void LogStartupPhase(string phase)
    {
        string? automationRoot = Environment.GetEnvironmentVariable("JITHUB_AUTOMATION_DATA_ROOT");
        if (string.IsNullOrWhiteSpace(automationRoot))
        {
            return;
        }

        try
        {
            string logDirectory = Path.Combine(Path.GetFullPath(automationRoot), "Local", "logs");
            Directory.CreateDirectory(logDirectory);
            File.AppendAllText(
                Path.Combine(logDirectory, "startup-phases.log"),
                $"{DateTimeOffset.UtcNow:O}\t{Environment.ProcessId}\t{phase}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}

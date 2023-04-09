using JitHub.Models;
using JitHub.Services;
using JitHub.Views;
using JitHub.Views.Pages;
using Microsoft.Extensions.DependencyInjection;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Toolkit.Uwp.Helpers;
using System;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.Core;
using Windows.ApplicationModel.Store;
using Windows.Storage;
using Windows.UI;
using Windows.UI.ViewManagement;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;

namespace JitHub
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    sealed partial class App : Application
    {
        private Frame _rootFrame;
        private bool _servicedConfigured = false;
        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            this.InitializeComponent();
            this.Suspending += OnSuspending;
            var store = ApplicationDataStorageHelper.GetCurrent(new Microsoft.Toolkit.Helpers.SystemSerializer());
            var theme = store.Read<string>(ThemeService.Key);
            if (theme != null && theme != ThemeConst.System)
            {
                RequestedTheme = ThemeService.GetApplicationThemeStatic(theme);
            }
        }

        private void InitializeTitleBar()
        {
            ApplicationViewTitleBar titleBar = ApplicationView.GetForCurrentView().TitleBar;
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            var coreTitleBar = CoreApplication.GetCurrentView().TitleBar;
            coreTitleBar.ExtendViewIntoTitleBar = true;
        }

        private void InitializeServices()
        {
            if (!_servicedConfigured)
            {
                var services = new ServiceCollection();

                services.AddSingleton(new NavigationService(_rootFrame));
                services.AddSingleton(new ModalService());
                services.AddSingleton(new FeatureService());
                services.AddScoped<INotificationService, NotificationService>();
                services.AddScoped<ISettingService, SettingService>();
                services.AddScoped<IGitHubService, GitHubService>();
                services.AddScoped<IAppConfig, AppConfig>();
                services.AddScoped<IAccountService, AccountService>();
                services.AddScoped<IAuthService, AuthService>();
                services.AddScoped<IThemeService, ThemeService>();
                services.AddScoped<ICommandService, CommandService>();
                services.AddSingleton<GlobalViewModel>();

                Ioc.Default.ConfigureServices(services.BuildServiceProvider());
                _servicedConfigured = true;
            }
        }

        protected override async void OnActivated(IActivatedEventArgs args)
        {
            base.OnActivated(args);
            var authService = Ioc.Default.GetService<IAuthService>();
            if (args.Kind == ActivationKind.Protocol)
            {
                var eventArgs = args as ProtocolActivatedEventArgs;
                var query = eventArgs.Uri.Query;
                var success = await authService.Authorize(query);
                if (success)
                {
                    _rootFrame.Navigate(typeof(ShellPage), null, new SuppressNavigationTransitionInfo());
                }
            }
        }

        /// <summary>
        /// Invoked when the application is launched normally by the end user.  Other entry points
        /// will be used such as when the application is launched to open a specific file.
        /// </summary>
        /// <param name="e">Details about the launch request and process.</param>
        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            _rootFrame = App.Window.Content as Frame;

            // Do not repeat app initialization when the Window already has content,
            // just ensure that the window is active
            if (_rootFrame == null)
            {
                // Create a Frame to act as the navigation context and navigate to the first page
                _rootFrame = new Frame();

                _rootFrame.NavigationFailed += OnNavigationFailed;

                if (e.PreviousExecutionState == ApplicationExecutionState.Terminated)
                {
                    //TODO: Load state from previously suspended application
                }

                // Place the frame in the current Window
                App.Window.Content = _rootFrame;
            }

            InitializeTitleBar();
            InitializeServices();
            
            if (e.PrelaunchActivated == false)
            {
                if (_rootFrame.Content == null)
                {
                    // When the navigation stack isn't restored navigate to the first page,
                    // configuring the new page by passing required information as a navigation
                    // parameter
                    var _authService = Ioc.Default.GetService<IAuthService>();
                    if (_authService.Authenticated)
                        _rootFrame.Navigate(typeof(ShellPage));
                    else
                        _rootFrame.Navigate(typeof(LoginPage), e.Arguments);
                }
                // Ensure the current window is active
                App.Window.Activate();
            }
        }

        /// <summary>
        /// Invoked when Navigation to a certain page fails
        /// </summary>
        /// <param name="sender">The Frame which failed navigation</param>
        /// <param name="e">Details about the navigation failure</param>
        void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            throw new Exception("Failed to load Page " + e.SourcePageType.FullName);
        }

        /// <summary>
        /// Invoked when application execution is being suspended.  Application state is saved
        /// without knowing whether the application will be terminated or resumed with the contents
        /// of memory still intact.
        /// </summary>
        /// <param name="sender">The source of the suspend request.</param>
        /// <param name="e">Details about the suspend request.</param>
        private void OnSuspending(object sender, SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();
            //TODO: Save application state and stop any background activity
            deferral.Complete();
        }
    }
}

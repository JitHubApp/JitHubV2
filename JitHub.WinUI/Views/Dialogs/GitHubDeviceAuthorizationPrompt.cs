using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using JitHub.WinUI.Helpers;

namespace JitHub.WinUI.Views.Dialogs;

public sealed class GitHubDeviceAuthorizationPrompt : IDeviceAuthorizationPrompt
{
    private readonly IGitHubDeviceFlowClient _client;
    private readonly IExternalUriLauncher _launcher;
    private readonly LocalizationService _strings;

    public GitHubDeviceAuthorizationPrompt(
        IGitHubDeviceFlowClient client,
        IExternalUriLauncher launcher,
        LocalizationService strings)
    {
        _client = client;
        _launcher = launcher;
        _strings = strings;
    }

    public async Task<GitHubTokenSession?> AuthorizeAsync(
        string clientId,
        IReadOnlyCollection<string> scopes,
        CancellationToken cancellationToken = default)
    {
        DeviceAuthorizationChallenge challenge = await _client.RequestCodeAsync(
            clientId, scopes, cancellationToken);
        App app = (App)Application.Current;
        XamlRoot root = app.CurrentMainWindow.DialogXamlRoot ??
            ((FrameworkElement)app.CurrentMainWindow.Content).XamlRoot;
        double gap = (double)app.Resources["AppGap16"];
        TextBlock instructions = new()
        {
            Text = T("Auth/Device/Instructions", "Open github.com/login/device and enter this code. Complete the request only if you started sign-in in JitHub."),
            TextWrapping = TextWrapping.WrapWholeWords
        };
        TextBlock code = new()
        {
            Text = challenge.UserCode,
            Style = (Style)app.Resources["SectionTitleTextBlockStyle"],
            IsTextSelectionEnabled = true
        };
        AutomationProperties.SetAutomationId(code, "GitHubDeviceCode");
        AutomationProperties.SetName(code, string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            T("Auth/Device/CodeName", "GitHub device code {0}"), challenge.UserCode));
        Button copy = new()
        {
            Content = T("Auth/Device/CopyCode", "Copy code"),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        AutomationProperties.SetAutomationId(copy, "CopyGitHubDeviceCode");
        AutomationProperties.SetName(copy, T("Auth/Device/CopyCodeName", "Copy GitHub device code"));
        copy.Click += (_, _) =>
        {
            PlatformHelper.CopyString(challenge.UserCode);
        };
        TextBlock status = new()
        {
            Text = T("Auth/Device/Waiting", "Waiting for GitHub approval…"),
            TextWrapping = TextWrapping.WrapWholeWords
        };
        AutomationProperties.SetAutomationId(status, "GitHubDeviceStatus");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        StackPanel content = new() { Spacing = gap };
        content.Children.Add(instructions);
        content.Children.Add(code);
        content.Children.Add(copy);
        content.Children.Add(status);
        ContentDialog dialog = new()
        {
            Title = T("Auth/Device/Title", "Sign in with GitHub"),
            Content = content,
            PrimaryButtonText = T("Auth/Device/OpenGitHub", "Open GitHub"),
            CloseButtonText = T("Auth/Device/Cancel", "Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        AppDialogStyleCatalog.Apply(dialog);
        AutomationProperties.SetAutomationId(dialog, "GitHubDeviceAuthorizationDialog");
        AutomationProperties.SetName(dialog, T("Auth/Device/DialogName", "GitHub device authorization"));
        dialog.PrimaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            UiTaskGuard.Run(async () =>
            {
                try
                {
                    if (!await _launcher.LaunchAsync(challenge.VerificationUri, cancellationToken))
                    {
                        status.Text = T("Auth/Device/LaunchFailed", "Could not open GitHub. Open github.com/login/device in your browser.");
                    }
                }
                catch (Exception)
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        status.Text = T("Auth/Device/LaunchFailed", "Could not open GitHub. Open github.com/login/device in your browser.");
                    }
                }
            }, "github-device-launch");
        };

        using CancellationTokenSource polling = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<GitHubTokenSession> tokenTask = _client.PollAsync(clientId, challenge, polling.Token);
        Task<ContentDialogResult> dialogTask = AppContentDialogPresenter.ShowAsync(
            dialog, root, AppDialogLayoutKind.CompactForm);
        Task completed = await Task.WhenAny(tokenTask, dialogTask);
        if (completed == dialogTask)
        {
            await polling.CancelAsync();
            try
            {
                await tokenTask;
            }
            catch (Exception exception) when (exception is OperationCanceledException or DeviceFlowException)
            {
            }

            return null;
        }

        if (tokenTask.IsFaulted)
        {
            DeviceFlowException? flowError = tokenTask.Exception?.GetBaseException() as DeviceFlowException;
            status.Text = flowError?.Code switch
            {
                "access_denied" => T("Login.DeviceDeniedError", "GitHub authorization was cancelled. Try again."),
                "expired_token" => T("Login.DeviceExpiredError", "The GitHub sign-in code expired. Try again."),
                "device_flow_disabled" => T("Login.DeviceDisabledError", "GitHub device sign-in is unavailable for this app."),
                _ => T("Auth/Device/Failed", "GitHub could not complete sign-in. Try again.")
            };
            dialog.PrimaryButtonText = string.Empty;
            dialog.CloseButtonText = T("Auth/Device/Close", "Close");
            await dialogTask;
            return await tokenTask;
        }

        if (tokenTask.IsCanceled)
        {
            dialog.Hide();
            await dialogTask;
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }

        status.Text = T("Auth/Device/Approved", "Approved by GitHub. Finishing sign-in…");
        await Task.Delay(TimeSpan.FromMilliseconds(600), cancellationToken);
        dialog.Hide();
        await dialogTask;
        return await tokenTask;
    }

    private string T(string key, string fallback) => _strings.GetStringOrDefault(key, fallback);
}

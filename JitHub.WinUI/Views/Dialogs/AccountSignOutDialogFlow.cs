using System;
using System.Globalization;
using System.Threading.Tasks;
using JitHub.Services;
using JitHub.WinUI.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.WinUI.Views.Dialogs;

internal static class AccountSignOutDialogFlow
{
    private static readonly System.Threading.SemaphoreSlim DialogGate = new(1, 1);

    public static async Task ShowAsync(XamlRoot xamlRoot)
    {
        ArgumentNullException.ThrowIfNull(xamlRoot);
        if (!await DialogGate.WaitAsync(0).ConfigureAwait(true))
        {
            return;
        }

        try
        {
            App app = (App)Application.Current;
            IAuthService auth = app.GetService<IAuthService>();
            IAccountService account = app.GetService<IAccountService>();
            IAccountDataRemovalCoordinator removal = app.GetService<IAccountDataRemovalCoordinator>();

            CheckBox removeLocalData = new()
            {
                Content = T(
                    "Dialogs/SignOut/RemoveLocalData",
                    "Remove local data for this GitHub account"),
                IsChecked = false,
                Margin = new Thickness(0, 12, 0, 0)
            };
            AutomationProperties.SetAutomationId(removeLocalData, "SignOutRemoveAccountDataCheckBox");
            AutomationProperties.SetName(
                removeLocalData,
                T("Dialogs/SignOut/RemoveLocalData", "Remove local data for this GitHub account"));

            StackPanel content = new()
            {
                Spacing = 4,
                Children =
                {
                    CreateBodyText(T(
                        "Dialogs/SignOut/Body",
                        "Signing out keeps this account's local data by default so a later sign-in stays fast.")),
                    removeLocalData,
                    CreateCaptionText(T(
                        "Dialogs/SignOut/RemovalDetails",
                        "Removal deletes this account's cached GitHub queries, payloads, images, repository files, Stars library, and pending Gist/fork recovery data. Other accounts are preserved. App diagnostics are retained because they contain no account or content identifiers."))
                }
            };
            TextBlock errorText = AppContentDialogPresenter.CreateInlineErrorPresenter(
                "SignOutConfirmationDialogError");
            content.Children.Add(errorText);

            ContentDialog confirmation = new()
            {
                XamlRoot = xamlRoot,
                Title = T("Dialogs/SignOut/Title", "Sign out of JitHub?"),
                Content = content,
                PrimaryButtonText = T("Dialogs/SignOut/PrimaryAction", "Sign out"),
                CloseButtonText = T("Common/Cancel", "Cancel"),
                DefaultButton = ContentDialogButton.Close
            };
            AppDialogStyleCatalog.Apply(confirmation);
            AutomationProperties.SetAutomationId(confirmation, "SignOutConfirmationDialog");
            AutomationProperties.SetName(
                confirmation,
                T("Dialogs/SignOut/AutomationName", "Sign out of JitHub"));
            confirmation.Opened += (_, _) => _ = removeLocalData.Focus(FocusState.Programmatic);

            string? pendingPartition = null;
            confirmation.Closed += (_, _) =>
            {
                if (pendingPartition is not null)
                {
                    removal.Resume(pendingPartition);
                }
            };

            await AppContentDialogPresenter.ShowForPrimaryActionAsync(
                confirmation,
                xamlRoot,
                async () =>
                {
                    if (removeLocalData.IsChecked != true)
                    {
                        auth.SignOut();
                        return DialogMutationResult.Success();
                    }

                    long accountId = auth.AuthenticatedUser?.Id ?? account.GetUser();
                    if (accountId <= 0)
                    {
                        auth.SignOut();
                        return DialogMutationResult.Success();
                    }

                    pendingPartition = accountId.ToString(CultureInfo.InvariantCulture);
                    AccountDataRemovalResult result = await removal.RemoveAsync(pendingPartition)
                        .ConfigureAwait(true);
                    if (result.IsComplete)
                    {
                        pendingPartition = null;
                        auth.SignOut();
                        return DialogMutationResult.Success();
                    }

                    return DialogMutationResult.Failure(
                        LocalizedResourceText.Format(
                            "Dialogs/SignOut/RemovalFailed",
                            "Some local account data could not be removed. {0} data groups are still pending. You are still signed in; retry, or cancel to continue cleanup in the background.",
                            result.Failures.Count));
                },
                errorText);
        }
        finally
        {
            DialogGate.Release();
        }
    }

    private static TextBlock CreateBodyText(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 480
    };

    private static TextBlock CreateCaptionText(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 480,
        Opacity = 0.72,
        FontSize = (double)Application.Current.Resources["AppFontSize12"]
    };

    private static string T(string resourceKey, string fallback) =>
        LocalizedResourceText.GetString(resourceKey, fallback);
}

using System;
using System.Threading.Tasks;
using JitHub.Services;
using JitHub.WinUI.Helpers;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace JitHub.WinUI.Views.Dialogs;

internal static class AppContentDialogPresenter
{
    public static async Task<ContentDialogResult> ShowAsync(
        ContentDialog dialog,
        XamlRoot xamlRoot,
        AppDialogLayoutKind layoutKind = AppDialogLayoutKind.Standard)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        ArgumentNullException.ThrowIfNull(xamlRoot);

        DialogPresentationCoordinator coordinator =
            ((App)Application.Current).GetService<DialogPresentationCoordinator>();
        if (!coordinator.TryBegin(DialogPresentationKind.NativeContentDialog, out long lease))
        {
            return ContentDialogResult.None;
        }

        MainWindow mainWindow = ((App)Application.Current).CurrentMainWindow;
        XamlRoot presentationRoot = mainWindow.DialogXamlRoot ?? xamlRoot;
        DialogFocusRestorationGate focusRestorationGate = DialogFocusRestorationGate.Shared;
        long focusGeneration = focusRestorationGate.BeginSession();
        Control? restoreTarget = TryGetFocusedControl(presentationRoot);
        DispatcherQueue dispatcherQueue = dialog.DispatcherQueue;
        TypedEventHandler<XamlRoot, XamlRootChangedEventArgs>? rootChangedHandler = null;

        try
        {
            dialog.XamlRoot = presentationRoot;
            AppDialogStyleCatalog.Apply(dialog);
            AppDialogStyleCatalog.ApplyLayout(dialog, presentationRoot, layoutKind);
            rootChangedHandler = (_, _) =>
            {
                AppDialogStyleCatalog.ApplyLayout(dialog, presentationRoot, layoutKind);
                mainWindow.ScheduleContentDialogFocusValidation(dialog);
            };
            presentationRoot.Changed += rootChangedHandler;
            return await mainWindow.ShowContentDialogAsync(dialog);
        }
        finally
        {
            if (rootChangedHandler is not null)
            {
                presentationRoot.Changed -= rootChangedHandler;
            }
            coordinator.Complete(lease);
            RestoreFocus(
                dispatcherQueue,
                restoreTarget,
                presentationRoot,
                focusRestorationGate,
                focusGeneration);
        }
    }

    public static async Task<ContentDialogResult> ShowForPrimaryActionAsync(
        ContentDialog dialog,
        XamlRoot xamlRoot,
        Func<Task<DialogMutationResult>> primaryAction,
        TextBlock errorPresenter,
        Func<bool>? canSubmit = null,
        AppDialogLayoutKind layoutKind = AppDialogLayoutKind.Standard)
    {
        ArgumentNullException.ThrowIfNull(primaryAction);
        ArgumentNullException.ThrowIfNull(errorPresenter);

        DialogSubmissionGate submissionGate = new();
        bool operationCompleted = false;
        bool primaryWasEnabled = dialog.IsPrimaryButtonEnabled;
        bool secondaryWasEnabled = dialog.IsSecondaryButtonEnabled;
        TypedEventHandler<ContentDialog, ContentDialogClosingEventArgs>? closingHandler = null;
        TypedEventHandler<ContentDialog, ContentDialogButtonClickEventArgs>? clickHandler = null;

        closingHandler = (_, args) =>
        {
            if (submissionGate.IsSubmitting)
            {
                args.Cancel = true;
            }
        };
        clickHandler = async (_, args) =>
        {
            if (!submissionGate.TryBegin())
            {
                args.Cancel = true;
                return;
            }

            ContentDialogButtonClickDeferral deferral = args.GetDeferral();
            args.Cancel = true;
            dialog.IsPrimaryButtonEnabled = false;
            dialog.IsSecondaryButtonEnabled = false;
            Button? closeButton = FindTemplateButton(dialog, "CloseButton");
            bool closeButtonWasEnabled = closeButton?.IsEnabled ?? true;
            if (closeButton is not null)
            {
                closeButton.IsEnabled = false;
            }
            AutomationProperties.SetItemStatus(
                dialog,
                LocalizedResourceText.GetString("Dialogs/Status/Working", "Working"));
            errorPresenter.Text = string.Empty;
            errorPresenter.Visibility = Visibility.Collapsed;
            try
            {
                DialogMutationResult result = await primaryAction();
                if (result.Succeeded)
                {
                    operationCompleted = true;
                    args.Cancel = false;
                }
                else
                {
                    ShowInlineError(errorPresenter, result.ErrorMessage);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Dialog mutation failed: {ex}");
                ShowInlineError(errorPresenter, null);
            }
            finally
            {
                submissionGate.Complete();
                if (!operationCompleted)
                {
                    dialog.IsPrimaryButtonEnabled = EvaluateCanSubmit(canSubmit, primaryWasEnabled);
                    dialog.IsSecondaryButtonEnabled = secondaryWasEnabled;
                    if (closeButton is not null)
                    {
                        closeButton.IsEnabled = closeButtonWasEnabled;
                    }
                }

                AutomationProperties.SetItemStatus(dialog, string.Empty);

                deferral.Complete();
            }
        };

        dialog.Closing += closingHandler;
        dialog.PrimaryButtonClick += clickHandler;
        try
        {
            return await ShowAsync(dialog, xamlRoot, layoutKind);
        }
        finally
        {
            dialog.Closing -= closingHandler;
            dialog.PrimaryButtonClick -= clickHandler;
        }
    }

    public static TextBlock CreateInlineErrorPresenter(string automationId)
    {
        TextBlock error = new()
        {
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AppDangerBrush"],
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };
        AutomationProperties.SetAutomationId(error, automationId);
        AutomationProperties.SetName(
            error,
            LocalizedResourceText.GetString("Dialogs/Error/AutomationName", "Dialog error"));
        AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Polite);
        return error;
    }

    private static Control? TryGetFocusedControl(XamlRoot xamlRoot)
    {
        try
        {
            return FocusManager.GetFocusedElement(xamlRoot) as Control;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Dialog opener focus could not be captured: {ex}");
            return null;
        }
    }

    private static void RestoreFocus(
        DispatcherQueue dispatcherQueue,
        Control? restoreTarget,
        XamlRoot xamlRoot,
        DialogFocusRestorationGate focusRestorationGate,
        long focusGeneration)
    {
        _ = dispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (!focusRestorationGate.CanRestore(focusGeneration, isDialogVisible: false))
                {
                    return;
                }

                if (restoreTarget?.XamlRoot == xamlRoot &&
                    restoreTarget is { IsEnabled: true, Visibility: Visibility.Visible } &&
                    restoreTarget.Focus(FocusState.Programmatic))
                {
                    return;
                }

                if (xamlRoot.Content is DependencyObject root &&
                    FocusManager.FindFirstFocusableElement(root) is Control fallback &&
                    fallback is { IsEnabled: true, Visibility: Visibility.Visible })
                {
                    _ = fallback.Focus(FocusState.Programmatic);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Dialog focus restoration was skipped: {ex}");
            }
        });
    }

    private static void ShowInlineError(TextBlock presenter, string? message)
    {
        string displayMessage = string.IsNullOrWhiteSpace(message)
            ? LocalizedResourceText.GetString(
                "Dialogs/Error/Generic",
                "JitHub could not complete this action. Try again.")
            : message;
        presenter.Text = displayMessage;
        AutomationProperties.SetName(presenter, displayMessage);
        presenter.Visibility = Visibility.Visible;
        _ = presenter.Focus(FocusState.Programmatic);
    }

    private static bool EvaluateCanSubmit(Func<bool>? canSubmit, bool fallback)
    {
        if (canSubmit is null)
        {
            return fallback;
        }

        try
        {
            return canSubmit();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Dialog validation state could not be evaluated: {ex}");
            return fallback;
        }
    }

    private static Button? FindTemplateButton(DependencyObject root, string name)
    {
        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < childCount; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is Button button && string.Equals(button.Name, name, StringComparison.Ordinal))
            {
                return button;
            }

            if (FindTemplateButton(child, name) is Button descendant)
            {
                return descendant;
            }
        }

        return null;
    }
}

internal readonly record struct DialogMutationResult(bool Succeeded, string? ErrorMessage)
{
    public static DialogMutationResult Success() => new(true, null);

    public static DialogMutationResult Failure(string? message) => new(false, message);
}

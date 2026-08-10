using System;
using System.Diagnostics;

namespace JitHub.WinUI.Helpers;

internal enum UserFacingErrorKind
{
    Action,
    Activation,
    Loading,
    Refresh,
    SignIn
}

internal static class UserFacingError
{
    public static string For(Exception exception, UserFacingErrorKind kind, string? context = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Log(exception.ToString(), context);
        return GetLocalizedMessage(kind);
    }

    public static string ForInternalMessage(
        string? internalMessage,
        UserFacingErrorKind kind,
        string? context = null)
    {
        if (!string.IsNullOrWhiteSpace(internalMessage))
        {
            Log(internalMessage, context);
        }

        return GetLocalizedMessage(kind);
    }

    private static string GetLocalizedMessage(UserFacingErrorKind kind) => kind switch
    {
        UserFacingErrorKind.Activation => LocalizedResourceText.GetString(
            "Errors.Activation",
            "JitHub could not open this request. Try again."),
        UserFacingErrorKind.Loading => LocalizedResourceText.GetString(
            "Errors.Loading",
            "JitHub could not load this content. Try again."),
        UserFacingErrorKind.Refresh => LocalizedResourceText.GetString(
            "Errors.Refresh",
            "JitHub could not refresh this content. Existing data is still available."),
        UserFacingErrorKind.SignIn => LocalizedResourceText.GetString(
            "Errors.SignIn",
            "JitHub could not sign you in. Try again."),
        _ => LocalizedResourceText.GetString(
            "Errors.Action",
            "JitHub could not complete this action. Try again.")
    };

    private static void Log(string detail, string? context)
    {
        string scope = string.IsNullOrWhiteSpace(context) ? "ui" : context.Trim();
        Debug.WriteLine($"[UserFacingError/{scope}] {detail}");
    }
}
